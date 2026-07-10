using Microsoft.Extensions.Options;
using Syren.Server.Configuration;

namespace Syren.Server.Services;

/// <summary>
/// Hosted service that manages the MQTT client lifecycle
/// </summary>
public class MqttHostedService : IHostedService
{
    private readonly IMqttClientService _mqttClientService;
    private readonly MqttOptions _options;
    private readonly ILogger<MqttHostedService> _logger;
    private CancellationTokenSource? _retryCancellationTokenSource;
    private Task? _retryTask;

    public MqttHostedService(
        IMqttClientService mqttClientService,
        IOptions<MqttOptions> options,
        ILogger<MqttHostedService> logger)
    {
        _mqttClientService = mqttClientService;
        _options = options.Value;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("MQTT Hosted Service starting");

        if (!_options.AutoReconnect)
        {
            await _mqttClientService.ConnectAsync(cancellationToken);
            return;
        }

        _retryCancellationTokenSource = new CancellationTokenSource();
        _retryTask = RunConnectionLoopAsync(_retryCancellationTokenSource.Token);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("MQTT Hosted Service stopping");

        if (_retryCancellationTokenSource != null)
        {
            await _retryCancellationTokenSource.CancelAsync();
        }

        if (_retryTask != null)
        {
            try
            {
                await _retryTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("MQTT connection loop stopped");
            }
        }

        try
        {
            await _mqttClientService.DisconnectAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping MQTT service");
        }
    }

    private async Task RunConnectionLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!_mqttClientService.IsConnected)
            {
                try
                {
                    await _mqttClientService.ConnectAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "MQTT connection attempt failed");
                }
            }

            try
            {
                await Task.Delay(
                    TimeSpan.FromSeconds(_options.ReconnectDelaySeconds),
                    cancellationToken
                );
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
