using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Syren.Server.Configuration;
using Syren.Server.Models;

namespace Syren.Server.Services;

public interface IConfigurationPublisher
{
    Task ClearRetiredPositionsAsync(CancellationToken cancellationToken = default);
    Task PublishStatusAsync(CancellationToken cancellationToken = default);
    Task PublishStateAsync(CancellationToken cancellationToken = default);
}

public sealed class ConfigurationPublisher : IConfigurationPublisher, IHostedService
{
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(100);

    private readonly IMqttClientService _mqttClientService;
    private readonly IDistanceService _distanceService;
    private readonly ISystemConfigurationService _configurationService;
    private readonly ISystemStateStore _stateStore;
    private readonly MqttOptions _options;
    private readonly ServerSession _session;
    private readonly ILogger<ConfigurationPublisher> _logger;
    private readonly Channel<PublishSignal> _signals = Channel.CreateUnbounded<PublishSignal>(
        new UnboundedChannelOptions { SingleReader = true }
    );
    private readonly object _publishedLock = new();
    private string? _publishedStateId;
    private long? _publishedRevision;
    private string[]? _publishedSpeakerIds;
    private CancellationTokenSource? _loopCancellation;
    private Task? _loop;

    public ConfigurationPublisher(
        IMqttClientService mqttClientService,
        IDistanceService distanceService,
        ISystemConfigurationService configurationService,
        ISystemStateStore stateStore,
        IOptions<MqttOptions> options,
        ServerSession session,
        ILogger<ConfigurationPublisher> logger)
    {
        _mqttClientService = mqttClientService;
        _distanceService = distanceService;
        _configurationService = configurationService;
        _stateStore = stateStore;
        _options = options.Value;
        _session = session;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stateStore.Changed += OnStateChanged;
        _configurationService.RuntimeChanged += OnRuntimeChanged;
        _loopCancellation = new CancellationTokenSource();
        _loop = RunAsync(_loopCancellation.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stateStore.Changed -= OnStateChanged;
        _configurationService.RuntimeChanged -= OnRuntimeChanged;
        if (_loopCancellation != null)
        {
            await _loopCancellation.CancelAsync();
        }
        if (_loop != null)
        {
            try
            {
                await _loop.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("Configuration publisher stopped");
            }
        }
    }

    public async Task ClearRetiredPositionsAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> retiredIds = await _distanceService.GetRetiredSpeakerIdsAsync(cancellationToken);
        if (retiredIds.Count == 0)
        {
            return;
        }
        foreach (string sensorId in retiredIds)
        {
            await _mqttClientService.ClearRetainedAsync(
                $"{_options.GetSpeakerPositionTopic}/{sensorId}",
                cancellationToken
            );
        }
        await _distanceService.ConfirmRetiredSpeakerIdsClearedAsync(retiredIds, cancellationToken);
    }

    public async Task PublishStatusAsync(CancellationToken cancellationToken = default)
    {
        string[] connectedSpeakerIds = await GetConnectedSpeakerIdsAsync(cancellationToken);
        await PublishStatusAsync(connectedSpeakerIds, cancellationToken);
    }

    public async Task PublishStateAsync(CancellationToken cancellationToken = default)
    {
        await PublishConfigurationAsync(force: true, cancellationToken);
        await PublishRuntimeAsync(cancellationToken);
    }

    private void OnStateChanged() => _signals.Writer.TryWrite(PublishSignal.Configuration);

    private void OnRuntimeChanged() => _signals.Writer.TryWrite(PublishSignal.Runtime);

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (await _signals.Reader.WaitToReadAsync(cancellationToken))
        {
            await Task.Delay(DebounceDelay, cancellationToken);
            bool configuration = false;
            bool runtime = false;
            while (_signals.Reader.TryRead(out PublishSignal signal))
            {
                configuration |= signal == PublishSignal.Configuration;
                runtime |= signal == PublishSignal.Runtime;
            }

            if (!_mqttClientService.IsConnected)
            {
                continue;
            }
            try
            {
                if (configuration)
                {
                    await ClearRetiredPositionsAsync(cancellationToken);
                    await PublishStatusIfSpeakersChangedAsync(cancellationToken);
                    await PublishConfigurationAsync(force: false, cancellationToken);
                }
                if (runtime)
                {
                    await PublishRuntimeAsync(cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Unable to publish configuration state");
            }
        }
    }

    private async Task<string[]> GetConnectedSpeakerIdsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<SpeakerPosition> positions =
            await _distanceService.GetConnectedSpeakerPositionsAsync(cancellationToken);
        return positions
            .Select(position => Identifiers.Normalize(position.SpeakerId))
            .Distinct()
            .Order()
            .ToArray();
    }

    private async Task PublishStatusIfSpeakersChangedAsync(CancellationToken cancellationToken)
    {
        string[] connectedSpeakerIds = await GetConnectedSpeakerIdsAsync(cancellationToken);
        lock (_publishedLock)
        {
            if (_publishedSpeakerIds != null && _publishedSpeakerIds.SequenceEqual(connectedSpeakerIds))
            {
                return;
            }
        }
        await PublishStatusAsync(connectedSpeakerIds, cancellationToken);
    }

    private async Task PublishStatusAsync(string[] connectedSpeakerIds, CancellationToken cancellationToken)
    {
        await _mqttClientService.PublishAsync(
            _options.ServerStatusTopic,
            ServerStatusMessage.Create(_session.Id, _distanceService.StateId, online: true, connectedSpeakerIds,
                _stateStore.Current.Version == 3 ? 3 : null),
            retain: true,
            cancellationToken: cancellationToken
        );
        lock (_publishedLock)
        {
            _publishedSpeakerIds = connectedSpeakerIds;
        }
    }

    private async Task PublishConfigurationAsync(bool force, CancellationToken cancellationToken)
    {
        SystemConfigurationSnapshot snapshot = _configurationService.GetConfiguration();
        if (!force && IsPublished(snapshot))
        {
            return;
        }
        await _mqttClientService.PublishAsync(
            _options.ConfigurationTopic,
            snapshot,
            retain: true,
            cancellationToken: cancellationToken
        );
        lock (_publishedLock)
        {
            _publishedStateId = snapshot.StateId;
            _publishedRevision = snapshot.Revision;
        }
    }

    private Task PublishRuntimeAsync(CancellationToken cancellationToken) => _mqttClientService.PublishAsync(
        _options.RuntimeTopic,
        _configurationService.CurrentRuntime,
        retain: true,
        cancellationToken: cancellationToken
    );

    private bool IsPublished(SystemConfigurationSnapshot snapshot)
    {
        lock (_publishedLock)
        {
            return _publishedRevision == snapshot.Revision && _publishedStateId == snapshot.StateId;
        }
    }

    private enum PublishSignal
    {
        Configuration,
        Runtime,
    }
}
