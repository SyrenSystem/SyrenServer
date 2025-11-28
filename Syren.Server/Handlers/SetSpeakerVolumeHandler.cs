using System.Text.Json;
using MQTTnet;
using Microsoft.Extensions.Options;
using Syren.Server.Configuration;
using Syren.Server.Models;
using Syren.Server.Services;
using Syren.Server.Utils;

namespace Syren.Server.Handlers;

/// <summary>
/// Handler for speaker volume setting requests from SyrenApp
/// Topic: SyrenSystem/SyrenApp/SetSpeakerVolume
/// </summary>
public class SetSpeakerVolumeHandler : IMqttMessageHandler
{
    private readonly IDistanceService _distanceService;
    private readonly MqttOptions _mqttOptions;
    private readonly ILogger<SetSpeakerVolumeHandler> _logger;

    public string Topic { get; }

    public SetSpeakerVolumeHandler(
        IDistanceService distanceService,
        IOptions<MqttOptions> mqttOptions,
        ILogger<SetSpeakerVolumeHandler> logger)
    {
        _distanceService = distanceService;
        _mqttOptions = mqttOptions.Value;
        _logger = logger;
        Topic = _mqttOptions.SetSpeakerVolumeTopic;
    }

    public Task HandleMessageAsync(MqttApplicationMessage message, CancellationToken cancellationToken = default)
    {
        var payload = PayloadUtils.GetPayloadAsString(message.Payload);
        _logger.LogDebug("Received SetSpeakerVolume data:\n{Payload}\n", payload);
        
        try
        {
            var speakerVolumeData = JsonSerializer.Deserialize<SetSpeakerVolumeData>(payload);

            if (speakerVolumeData.Volume < 0.0)
            {
                _logger.LogError("Cannot set volume to a value {Volume} < 0", speakerVolumeData.Volume);
                return Task.CompletedTask;
            }

            _distanceService.SetSpeakerVolumeAsync(speakerVolumeData.SensorId, speakerVolumeData.Volume);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse sensor data from topic {Topic}. Payload:\n{Payload}\n",
                message.Topic, payload);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling sensor data from topic {Topic}", message.Topic);
        }

        return Task.CompletedTask;
    }
}
