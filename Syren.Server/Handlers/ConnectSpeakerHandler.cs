using System.Text.Json;
using Microsoft.Extensions.Options;
using MQTTnet;
using Syren.Server.Configuration;
using Syren.Server.Models;
using Syren.Server.Services;

namespace Syren.Server.Handlers;

public sealed class ConnectSpeakerHandler : IMqttMessageHandler
{
    private readonly IDistanceService _distanceService;
    private readonly MqttOptions _mqttOptions;
    private readonly ILogger<ConnectSpeakerHandler> _logger;

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

    public string Topic { get; }

    public async Task HandleMessageAsync(
        MqttApplicationMessage message,
        IMqttClientService client,
        CancellationToken cancellationToken = default)
    {
        string payload = message.ConvertPayloadToString();
        try
        {
            ConnectSpeakerData data = JsonSerializer.Deserialize<ConnectSpeakerData>(payload);
            if (string.IsNullOrWhiteSpace(data.SensorId) ||
                (data.Volume.HasValue &&
                    (!double.IsFinite(data.Volume.Value) || data.Volume.Value is < 0 or > 100)))
            {
                _logger.LogWarning("Dropping invalid connect payload from {Topic}", message.Topic);
                return;
            }

            SpeakerState? state = await _distanceService.ConnectSpeakerAsync(
                data.SensorId,
                data.Volume,
                cancellationToken
            );
            if (state == null)
            {
                return;
            }

            var position = new SpeakerPosition
            {
                SpeakerId = state.Speaker.SensorId!,
                Position = PositionVector.FromVector3(state.Position),
            };
            await client.PublishAsync(
                $"{_mqttOptions.GetSpeakerPositionTopic}/{state.Speaker.SensorId!}",
                position,
                retain: true,
                cancellationToken: cancellationToken
            );
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(exception, "Dropping malformed connect payload from {Topic}", message.Topic);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Unable to connect speaker from {Topic}", message.Topic);
        }
    }
}
