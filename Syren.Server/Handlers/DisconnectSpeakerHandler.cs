using MQTTnet;
using Microsoft.Extensions.Options;
using Syren.Server.Configuration;
using Syren.Server.Services;
using System.Text.Json;
using Syren.Server.Models;
using Syren.Server.Utils;

namespace Syren.Server.Handlers;

/// <summary>
/// Handler for sensor data messages from SyrenApp
/// Topic: SyrenSystem/SyrenApp/DisconnectSpeaker
/// </summary>
public class DisconnectSpeakerHandler : IMqttMessageHandler
{
    private readonly IDistanceService _distanceService;
    private readonly MqttOptions _mqttOptions;
    private readonly ILogger<DisconnectSpeakerHandler> _logger;

    public string Topic { get; }

    public DisconnectSpeakerHandler(
        IDistanceService distanceService,
        IOptions<MqttOptions> mqttOptions,
        ILogger<DisconnectSpeakerHandler> logger)
    {
        _distanceService = distanceService;
        _mqttOptions = mqttOptions.Value;
        _logger = logger;
        Topic = _mqttOptions.DisconnectSpeakerTopic;
    }

    public Task HandleMessageAsync(MqttApplicationMessage message, IMqttClientService client, CancellationToken cancellationToken = default)
    {
        var payload = PayloadUtils.GetPayloadAsString(message.Payload);
        _logger.LogDebug("Received speaker removal request:\n{Payload}\n", payload);

        try
        {
            var disconnectSpeakerData = JsonSerializer.Deserialize<DisconnectSpeakerData>(payload);

            _distanceService.DisconnectSpeakerAsync(disconnectSpeakerData.SensorId);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse 'disconnect speaker' data from topic {Topic}. Payload:\n{Payload}\n",
                message.Topic, payload);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling disconnect speaker request from topic {Topic}", message.Topic);
        }

        return Task.CompletedTask;
    }
}
