using Microsoft.Extensions.Options;
using Syren.Server.Configuration;
using Syren.Server.Models;

namespace Syren.Server.Services;

public sealed class MqttHostedService : IHostedService
{
    private readonly IMqttClientService _mqttClientService;
    private readonly IDistanceService _distanceService;
    private readonly IConfigurationPublisher _publisher;
    private readonly MqttOptions _options;
    private readonly ServerSession _session;
    private readonly ILogger<MqttHostedService> _logger;
    private CancellationTokenSource? _retryCancellationTokenSource;
    private Task? _retryTask;

    public MqttHostedService(
        IMqttClientService mqttClientService,
        IDistanceService distanceService,
        IConfigurationPublisher publisher,
        IOptions<MqttOptions> options,
        ServerSession session,
        ILogger<MqttHostedService> logger)
    {
        _mqttClientService = mqttClientService;
        _distanceService = distanceService;
        _publisher = publisher;
        _options = options.Value;
        _session = session;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.AutoReconnect)
        {
            await ConnectAndSynchronizeAsync(cancellationToken);
            return;
        }

        _retryCancellationTokenSource = new CancellationTokenSource();
        _retryTask = RunConnectionLoopAsync(_retryCancellationTokenSource.Token);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
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
                _logger.LogDebug("MQTT retry loop stopped");
            }
        }

        if (_mqttClientService.IsConnected)
        {
            try
            {
                await _mqttClientService.PublishAsync(
                    _options.ServerStatusTopic,
                    ServerStatusMessage.Create(_session.Id, _distanceService.StateId, online: false, []),
                    retain: true,
                    cancellationToken: cancellationToken
                );
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Unable to publish offline server status");
            }
        }
        await _mqttClientService.DisconnectAsync(cancellationToken);
    }

    private async Task RunConnectionLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!_mqttClientService.IsConnected)
            {
                try
                {
                    await ConnectAndSynchronizeAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "MQTT connection or state synchronization failed");
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

    private async Task ConnectAndSynchronizeAsync(CancellationToken cancellationToken)
    {
        await _mqttClientService.ConnectAsync(cancellationToken);
        try
        {
            await SynchronizeBrokerStateAsync(cancellationToken);
        }
        catch
        {
            await _mqttClientService.DisconnectAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task SynchronizeBrokerStateAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<SpeakerPosition> activePositions =
            await _distanceService.GetConnectedSpeakerPositionsAsync(cancellationToken);
        IReadOnlyList<string> configuredIds =
            await _distanceService.GetConfiguredSpeakerIdsAsync(cancellationToken);
        var activeIds = activePositions
            .Select(position => position.SpeakerId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (string sensorId in configuredIds.Where(sensorId => !activeIds.Contains(sensorId)))
        {
            await _mqttClientService.ClearRetainedAsync(
                $"{_options.GetSpeakerPositionTopic}/{sensorId}",
                cancellationToken
            );
        }
        await _publisher.ClearRetiredPositionsAsync(cancellationToken);
        foreach (SpeakerPosition position in activePositions)
        {
            await _mqttClientService.PublishAsync(
                $"{_options.GetSpeakerPositionTopic}/{position.SpeakerId}",
                position,
                retain: true,
                cancellationToken: cancellationToken
            );
        }

        await _publisher.PublishStatusAsync(cancellationToken);
        await _publisher.PublishStateAsync(cancellationToken);
    }
}
