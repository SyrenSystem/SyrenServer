using System.Buffers;
using System.Text;
using MQTTnet;
using Microsoft.Extensions.Options;
using Syren.Server.Configuration;
using Syren.Server.Services;
using System.Text.Json;
using Syren.Server.Models;

namespace Syren.Server.Handlers;

/// <summary>
/// Handler for sensor data messages from SyrenApp
/// Topic: SyrenSystem/SyrenApp/AddSpeaker
/// </summary>
public class AddSpeakerHandler : IMqttMessageHandler
{
    private readonly IDistanceService _distanceService;
    private readonly MqttOptions _mqttOptions;
    private readonly ILogger<AddSpeakerHandler> _logger;

    public string Topic { get; }

    public AddSpeakerHandler(
        IDistanceService distanceService,
        IOptions<MqttOptions> mqttOptions,
        ILogger<AddSpeakerHandler> logger)
    {
        _distanceService = distanceService;
        _mqttOptions = mqttOptions.Value;
        _logger = logger;
        Topic = _mqttOptions.AddSpeakerTopic;
    }

    public Task HandleMessageAsync(MqttApplicationMessage message, CancellationToken cancellationToken = default)
    {
        var payload = GetPayloadAsString(message.Payload);
        _logger.LogDebug("Received speaker adding request:\n{Payload}\n", payload);

        try
        {
            var addSpeakerData = JsonSerializer.Deserialize<AddSpeakerData>(payload);

            _distanceService.AddSpeaker(addSpeakerData.Id);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse 'add speaker' data form topic {Topic}. Payload:\n{Payload}\n",
                message.Topic, payload);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling add speaker request from topic {Topic}", message.Topic);
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
