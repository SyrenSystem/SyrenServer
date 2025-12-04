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

    public async Task HandleMessageAsync(MqttApplicationMessage message, IMqttClientService client, CancellationToken cancellationToken = default)
    {
        var payload = PayloadUtils.GetPayloadAsString(message.Payload);
        _logger.LogDebug("Received speaker adding request:\n{Payload}\n", payload);

        try
        {
            var connectSpeakerData = JsonSerializer.Deserialize<ConnectSpeakerData>(payload);

            SpeakerState? speakerState = await _distanceService.ConnectSpeakerAsync(connectSpeakerData.SensorId);
            if (speakerState == null) {
                _logger.LogInformation("Not publishing new speaker position. Connecting speaker failed.");
                return;
            }

            await PublishSpeakerPosition(speakerState, client);
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
    }

    private async Task PublishSpeakerPosition(SpeakerState speakerState, IMqttClientService client)
    {
        var speakerDistance = new SpeakerPosition
        {
            SpeakerId = speakerState.Speaker.SensorId.ToLower(),
            Position = new PositionVector{
                X = speakerState.Position.X,
                Y = speakerState.Position.Y,
                Z = speakerState.Position.Z,
            },
        };

        try
        {
            await client.PublishAsync(_mqttOptions.GetSpeakerPositionTopic, speakerDistance);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to publish new speaker position");
        }
    }
}
