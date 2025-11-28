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
/// Topic: SyrenSystem/SyrenApp/ConnectSpeaker
/// </summary>
public class ConnectSpeakerHandler : IMqttMessageHandler
{
    private readonly IDistanceService _distanceService;
    private readonly MqttOptions _mqttOptions;
    private readonly ILogger<ConnectSpeakerHandler> _logger;

    public string Topic { get; }

    public ConnectSpeakerHandler(
        IDistanceService distanceService,
        IOptions<MqttOptions> mqttOptions,
        ILogger<ConnectSpeakerHandler> logger)
    {
        _distanceService = distanceService;
        _mqttOptions = mqttOptions.Value;
        _logger = logger;
        Topic = _mqttOptions.ConnectSpeakerTopic;
    }

    public Task HandleMessageAsync(MqttApplicationMessage message, CancellationToken cancellationToken = default)
    {
        var payload = PayloadUtils.GetPayloadAsString(message.Payload);
        _logger.LogDebug("Received speaker adding request:\n{Payload}\n", payload);

        try
        {
            var connectSpeakerData = JsonSerializer.Deserialize<ConnectSpeakerData>(payload);

            _distanceService.ConnectSpeakerAsync(connectSpeakerData.SensorId);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse 'connect speaker' data from topic {Topic}. Payload:\n{Payload}\n",
                message.Topic, payload);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling connect speaker request from topic {Topic}", message.Topic);
        }

        return Task.CompletedTask;
    }

}
