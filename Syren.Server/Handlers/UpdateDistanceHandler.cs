using System.Text.Json;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Protocol;
using Syren.Server.Configuration;
using Syren.Server.Models;
using Syren.Server.Services;

namespace Syren.Server.Handlers;

public sealed class UpdateDistanceHandler : IMqttMessageHandler
{
    private readonly object _availabilityLock = new();
    private readonly IDistanceService _distanceService;
    private readonly MqttOptions _mqttOptions;
    private readonly ILogger<UpdateDistanceHandler> _logger;
    private bool _positionWasAvailable;

    public UpdateDistanceHandler(
        IDistanceService distanceService,
        IOptions<MqttOptions> mqttOptions,
        ILogger<UpdateDistanceHandler> logger)
    {
        _distanceService = distanceService;
        _mqttOptions = mqttOptions.Value;
        _logger = logger;
        Topic = _mqttOptions.UpdateDistanceTopic;
    }

    public string Topic { get; }

    public async Task HandleMessageAsync(
        MqttApplicationMessage message,
        IMqttClientService client,
        CancellationToken cancellationToken = default)
    {
        try
        {
            DistanceData data = JsonSerializer.Deserialize<DistanceData>(
                message.ConvertPayloadToString()
            );
            if (string.IsNullOrWhiteSpace(data.SpeakerId) ||
                !double.IsFinite(data.Distance) ||
                data.Distance < 0)
            {
                _logger.LogWarning("Dropping invalid distance payload from {Topic}", message.Topic);
                return;
            }

            await _distanceService.UpdateDistanceAsync(data, cancellationToken);
            System.Numerics.Vector3? position = _distanceService.GetUserPosition();
            if (position.HasValue)
            {
                lock (_availabilityLock)
                {
                    _positionWasAvailable = true;
                }
                await client.PublishAsync(
                    _mqttOptions.GetUserPositionTopic,
                    new UserPosition { Position = PositionVector.FromVector3(position.Value) },
                    qualityOfService: MqttQualityOfServiceLevel.AtMostOnce,
                    cancellationToken: cancellationToken
                );
                return;
            }

            bool clearPosition;
            lock (_availabilityLock)
            {
                clearPosition = _positionWasAvailable;
                _positionWasAvailable = false;
            }
            if (clearPosition)
            {
                await client.PublishEmptyAsync(
                    _mqttOptions.GetUserPositionTopic,
                    qualityOfService: MqttQualityOfServiceLevel.AtMostOnce,
                    cancellationToken: cancellationToken
                );
            }
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(exception, "Dropping malformed distance payload from {Topic}", message.Topic);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Unable to process distance from {Topic}", message.Topic);
        }
    }
}
