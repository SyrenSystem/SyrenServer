using System.Text.Json;
using Microsoft.Extensions.Options;
using MQTTnet;
using Syren.Server.Configuration;
using Syren.Server.Models;
using Syren.Server.Services;

namespace Syren.Server.Handlers;

public sealed class SetSpeakerVolumeHandler : IMqttMessageHandler
{
    private readonly IDistanceService _distanceService;
    private readonly ILogger<SetSpeakerVolumeHandler> _logger;

    public SetSpeakerVolumeHandler(
        IDistanceService distanceService,
        IOptions<MqttOptions> mqttOptions,
        ILogger<SetSpeakerVolumeHandler> logger)
    {
        _distanceService = distanceService;
        _logger = logger;
        Topic = mqttOptions.Value.SetSpeakerVolumeTopic;
    }

    public string Topic { get; }

    public async Task HandleMessageAsync(
        MqttApplicationMessage message,
        IMqttClientService client,
        CancellationToken cancellationToken = default)
    {
        try
        {
            SetSpeakerVolumeData data = JsonSerializer.Deserialize<SetSpeakerVolumeData>(
                message.ConvertPayloadToString()
            );
            if (string.IsNullOrWhiteSpace(data.SensorId) ||
                !double.IsFinite(data.Volume) ||
                data.Volume is < 0 or > 100)
            {
                _logger.LogWarning("Dropping invalid volume payload from {Topic}", message.Topic);
                return;
            }
            await _distanceService.SetSpeakerVolumeAsync(
                data.SensorId,
                data.Volume,
                cancellationToken
            );
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(exception, "Dropping malformed volume payload from {Topic}", message.Topic);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Unable to set speaker volume from {Topic}", message.Topic);
        }
    }
}
