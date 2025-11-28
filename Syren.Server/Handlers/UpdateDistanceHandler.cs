using System.Text.Json;
using MQTTnet;
using Microsoft.Extensions.Options;
using Syren.Server.Configuration;
using Syren.Server.Models;
using Syren.Server.Services;
using Syren.Server.Utils;

namespace Syren.Server.Handlers;

/// <summary>
/// Handler for sensor data messages from SyrenApp
/// Topic: SyrenSystem/SyrenApp/UpdateDistance
/// </summary>
public class UpdateDistanceHandler : IMqttMessageHandler
{
    private readonly IDistanceService _distanceService;
    private readonly MqttOptions _mqttOptions;
    private readonly ILogger<UpdateDistanceHandler> _logger;

    public string Topic { get; }

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

    public Task HandleMessageAsync(MqttApplicationMessage message, CancellationToken cancellationToken = default)
    {
        var payload = PayloadUtils.GetPayloadAsString(message.Payload);
        _logger.LogDebug("Received UpdateDistance data:\n{Payload}\n", payload);
        
        try
        {
            
            var distanceData = JsonSerializer.Deserialize<DistanceData>(payload);
            _distanceService.UpdateDistanceAsync(distanceData);
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse distance data from topic {Topic}. Payload:\n{Payload}\n",
                message.Topic, payload);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling distance data from topic {Topic}", message.Topic);
        }

        return Task.CompletedTask;
    }
}
