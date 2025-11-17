using System.Buffers;
using System.Text;
using System.Text.Json;
using MQTTnet;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Syren.Server.Configuration;
using Syren.Server.Models;
using Syren.Server.Services;

namespace Syren.Server.Handlers;

/// <summary>
/// Handler for sensor data messages from SyrenApp
/// Topic: SyrenSystem/SyrenApp/sensorData
/// </summary>
public class SensorDataHandler : IMqttMessageHandler
{
    private readonly IDistanceService _distanceService;
    private readonly MqttOptions _mqttOptions;
    private readonly ILogger<SensorDataHandler> _logger;
    private readonly Dictionary<string, int> _sensorIdToSpeakerIdMap = new();
    private readonly object _mapLock = new();

    public string Topic { get; }

    public SensorDataHandler(
        IDistanceService distanceService,
        IOptions<MqttOptions> mqttOptions,
        ILogger<SensorDataHandler> logger)
    {
        _distanceService = distanceService;
        _mqttOptions = mqttOptions.Value;
        _logger = logger;
        Topic = _mqttOptions.SensorDataTopic;
    }

    public Task HandleMessageAsync(MqttApplicationMessage message, CancellationToken cancellationToken = default)
    {
        var payload = GetPayloadAsString(message.Payload);
        _logger.LogDebug("Received sensor data: {Payload}", payload);
        try
        {
            
            var sensorDataArray = JsonSerializer.Deserialize<DistanceData[]>(payload);
            if (sensorDataArray != null)
            {
                _distanceService.UpdateDistances(sensorDataArray);
            }
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse sensor data from topic {Topic}. Payload: {Payload}",
            message.Topic, GetPayloadAsString(message.Payload));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling sensor data from topic {Topic}", message.Topic);
        }
        return Task.CompletedTask;
    }

    private static string GetPayloadAsString(ReadOnlySequence<byte> payload)
    {
        if (payload.IsEmpty)
        {
            return string.Empty;
        }

        if (payload.IsSingleSegment)
        {
            return Encoding.UTF8.GetString(payload.FirstSpan);
        }

        return Encoding.UTF8.GetString(payload.ToArray());
    }
}
