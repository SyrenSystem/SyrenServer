namespace Syren.Server.Services;

/// <summary>
/// Hosted service that manages the MQTT client lifecycle
/// </summary>
public class MqttHostedService : IHostedService
{
    private readonly IMqttClientService _mqttClientService;
    private readonly ILogger<MqttHostedService> _logger;

    public MqttHostedService(
        IMqttClientService mqttClientService,
        ILogger<MqttHostedService> logger)
    {
        _mqttClientService = mqttClientService;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("MQTT Hosted Service starting");
        
        try
        {
            await _mqttClientService.ConnectAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start MQTT service");
            // Don't throw - allow the application to continue running
            // The auto-reconnect feature will attempt to reconnect
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("MQTT Hosted Service stopping");
        
        try
        {
            await _mqttClientService.DisconnectAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping MQTT service");
        }
    }
}
