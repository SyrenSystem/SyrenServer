using System.Text.Json;
using Microsoft.Extensions.Options;
using MQTTnet;
using Syren.Server.Configuration;
using Syren.Server.Models;
using Syren.Server.Services;

namespace Syren.Server.Handlers;

public sealed class DisconnectSpeakerHandler : IMqttMessageHandler
{
    private readonly IDistanceService _distanceService;
    private readonly MqttOptions _mqttOptions;
    private readonly ILogger<DisconnectSpeakerHandler> _logger;

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

    public string Topic { get; }

    public async Task HandleMessageAsync(
        MqttApplicationMessage message,
        IMqttClientService client,
        CancellationToken cancellationToken = default)
    {
        try
        {
            DisconnectSpeakerData data = JsonSerializer.Deserialize<DisconnectSpeakerData>(
                message.ConvertPayloadToString()
            );
            if (string.IsNullOrWhiteSpace(data.SensorId))
            {
                _logger.LogWarning("Dropping disconnect payload without an ID");
                return;
            }

            string sensorId = Identifiers.Normalize(data.SensorId);
            DisconnectResult result = await _distanceService.DisconnectSpeakerAsync(
                sensorId,
                cancellationToken
            );
            if (result != DisconnectResult.UnknownSensor)
            {
                await client.ClearRetainedAsync(
                    $"{_mqttOptions.GetSpeakerPositionTopic}/{sensorId}",
                    cancellationToken
                );
            }
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(exception, "Dropping malformed disconnect payload from {Topic}", message.Topic);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Unable to disconnect speaker from {Topic}", message.Topic);
        }
    }
}
