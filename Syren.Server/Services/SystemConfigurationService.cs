using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Syren.Server.Configuration;
using Syren.Server.Models;
using Syren.Server.Models.SnapCast;

namespace Syren.Server.Services;

public sealed class SystemConfigurationService : BackgroundService, ISystemConfigurationService
{
    private readonly ISystemStateStore _stateStore;
    private readonly IDistanceService _distanceService;
    private readonly ISnapCastService _snapCastService;
    private readonly PlaybackOptions _options;
    private readonly ILogger<SystemConfigurationService> _logger;
    private readonly SemaphoreSlim _mutationLock = new(1, 1);
    private readonly SemaphoreSlim _reconcileLock = new(1, 1);
    private readonly SemaphoreSlim _reconcileSignal = new(0, 1);
    private readonly object _signalLock = new();
    private SystemRuntimeSnapshot _currentRuntime;

    public SystemConfigurationService(
        ISystemStateStore stateStore,
        IDistanceService distanceService,
        ISnapCastService snapCastService,
        IOptions<PlaybackOptions> options,
        ILogger<SystemConfigurationService> logger)
    {
        _stateStore = stateStore;
        _distanceService = distanceService;
        _snapCastService = snapCastService;
        _options = options.Value;
        _logger = logger;
        _currentRuntime = CreateOfflineRuntime();
    }

    public SystemRuntimeSnapshot CurrentRuntime => Volatile.Read(ref _currentRuntime);

    public event Action? RuntimeChanged;

    public SystemConfigurationSnapshot GetConfiguration()
    {
        PersistentSystemState current = _stateStore.Current;
        return new SystemConfigurationSnapshot
        {
            StateId = current.StateId,
            Revision = current.Revision,
            Speakers = current.Speakers.Select(ToConfiguration).ToArray(),
            Groups = current.Groups.Select(ToConfiguration).ToArray(),
            Sources = _options.Sources.Select(source => new AudioSourceConfiguration
            {
                Id = source.Id,
                Name = source.Name,
            }).ToArray(),
        };
    }

    public Task<CommandResultMessage> ConfigureSpeakerAsync(
        ConfigureSpeakerCommand command,
        CancellationToken cancellationToken = default) => MutateAsync(
        command,
        current =>
        {
            ValidateSpeakerCommand(command, current);
            string speakerId = string.IsNullOrWhiteSpace(command.SpeakerId)
                ? Guid.NewGuid().ToString("N")
                : Identifiers.Normalize(command.SpeakerId);
            PersistentSpeakerState? existing = current.Speakers.FirstOrDefault(speaker =>
                speaker.SpeakerId!.Equals(speakerId, StringComparison.OrdinalIgnoreCase)
            );
            string? sensorId = Identifiers.NormalizeOptional(command.SensorId);
            bool keepCalibration = existing?.SensorId != null &&
                existing.SensorId.Equals(sensorId, StringComparison.OrdinalIgnoreCase);
            var configured = new PersistentSpeakerState
            {
                SpeakerId = speakerId,
                Name = command.Name.Trim(),
                SensorId = sensorId,
                SnapClientId = Identifiers.Normalize(command.SnapClientId),
                FullVolumeDistance = command.FullVolumeDistance,
                MuteDistance = command.MuteDistance,
                Connected = keepCalibration && existing!.Connected,
                Position = keepCalibration ? existing!.Position : null,
                Volume = existing?.Volume ?? 100,
            };
            var speakers = current.Speakers
                .Where(speaker => !speaker.SpeakerId!.Equals(
                    speakerId,
                    StringComparison.OrdinalIgnoreCase
                ))
                .Append(configured)
                .OrderBy(speaker => speaker.Name)
                .ToList();
            return current with { Revision = current.Revision + 1, Speakers = speakers };
        },
        signalReconcile: true,
        cancellationToken
    );

    public Task<CommandResultMessage> DeleteSpeakerAsync(
        DeleteSpeakerCommand command,
        CancellationToken cancellationToken = default) => MutateAsync(
        command,
        current =>
        {
            string speakerId = Identifiers.Normalize(command.SpeakerId);
            if (!current.Speakers.Any(speaker => speaker.SpeakerId!.Equals(
                speakerId,
                StringComparison.OrdinalIgnoreCase
            )))
            {
                throw new InvalidOperationException("Speaker not found");
            }
            List<PersistentSpeakerState> speakers = current.Speakers
                .Where(speaker => !speaker.SpeakerId!.Equals(
                    speakerId,
                    StringComparison.OrdinalIgnoreCase
                ))
                .ToList();
            List<PersistentPlaybackGroup> groups = current.Groups.Select(group => group with
            {
                SpeakerIds = group.SpeakerIds.Where(id => !id.Equals(
                    speakerId,
                    StringComparison.OrdinalIgnoreCase
                )).ToList(),
            }).ToList();
            return current with
            {
                Revision = current.Revision + 1,
                Speakers = speakers,
                Groups = groups,
            };
        },
        signalReconcile: true,
        cancellationToken
    );

    public Task<CommandResultMessage> UpsertGroupAsync(
        UpsertGroupCommand command,
        CancellationToken cancellationToken = default) => MutateAsync(
        command,
        current =>
        {
            string groupId = string.IsNullOrWhiteSpace(command.GroupId)
                ? Guid.NewGuid().ToString("N")
                : Identifiers.Normalize(command.GroupId);
            ValidateGroupCommand(command, groupId, current);
            var group = new PersistentPlaybackGroup
            {
                Id = groupId,
                Name = command.Name.Trim(),
                SpeakerIds = command.SpeakerIds.Select(Identifiers.Normalize).Distinct().ToList(),
                SourcePriority = command.SourcePriority.Select(Identifiers.Normalize).Distinct().ToList(),
                SourceLevels = command.SourceLevels?.ToDictionary(pair => Identifiers.Normalize(pair.Key), pair => pair.Value)
                    ?? current.Groups.FirstOrDefault(existing => existing.Id == groupId)?.SourceLevels ?? [],
                VolumeMode = command.VolumeMode,
                MasterVolume = command.MasterVolume,
                Muted = command.Muted,
            };
            List<PersistentPlaybackGroup> groups = current.Groups
                .Where(existing => !existing.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase))
                .Append(group)
                .OrderBy(existing => existing.Name)
                .ToList();
            return current with { Revision = current.Revision + 1, Groups = groups };
        },
        signalReconcile: true,
        cancellationToken
    );

    public Task<CommandResultMessage> DeleteGroupAsync(
        DeleteGroupCommand command,
        CancellationToken cancellationToken = default) => MutateAsync(
        command,
        current =>
        {
            string groupId = Identifiers.Normalize(command.GroupId);
            if (!current.Groups.Any(group => group.Id.Equals(
                groupId,
                StringComparison.OrdinalIgnoreCase
            )))
            {
                throw new InvalidOperationException("Playback group not found");
            }
            return current with
            {
                Revision = current.Revision + 1,
                Groups = current.Groups.Where(group => !group.Id.Equals(
                    groupId,
                    StringComparison.OrdinalIgnoreCase
                )).ToList(),
            };
        },
        signalReconcile: true,
        cancellationToken
    );

    public Task<CommandResultMessage> SetSpeakerLevelAsync(
        SetSpeakerLevelCommand command,
        CancellationToken cancellationToken = default) => MutateAsync(
        command,
        current =>
        {
            if (!double.IsFinite(command.Level) || command.Level is < 0 or > 100)
            {
                throw new InvalidOperationException("Speaker level must be between 0 and 100");
            }
            string speakerId = Identifiers.Normalize(command.SpeakerId);
            bool found = false;
            List<PersistentSpeakerState> speakers = current.Speakers.Select(speaker =>
            {
                if (!speaker.SpeakerId!.Equals(speakerId, StringComparison.OrdinalIgnoreCase))
                {
                    return speaker;
                }
                found = true;
                return speaker with { Volume = command.Level };
            }).ToList();
            if (!found)
            {
                throw new InvalidOperationException("Speaker not found");
            }
            return current with { Revision = current.Revision + 1, Speakers = speakers };
        },
        signalReconcile: false,
        cancellationToken
    );

    public async Task ReconcileAsync(CancellationToken cancellationToken = default)
    {
        await _reconcileLock.WaitAsync(cancellationToken);
        try
        {
            SystemRuntimeSnapshot runtime;
            try
            {
                runtime = await ReconcileWithSnapserverAsync(cancellationToken);
            }
            catch (SnapServerUnavailableException)
            {
                UpdateRuntime(CreateOfflineRuntime());
                throw;
            }
            UpdateRuntime(runtime);
        }
        finally
        {
            _reconcileLock.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        TimeSpan interval = TimeSpan.FromSeconds(_options.ReconcileSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            bool wasOnline = CurrentRuntime.SnapserverOnline;
            try
            {
                await ReconcileAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (SnapServerUnavailableException exception)
            {
                if (wasOnline)
                {
                    _logger.LogWarning(
                        "Unable to reconcile playback configuration: {Message}",
                        exception.Message
                    );
                }
                else
                {
                    _logger.LogDebug("Snapserver is still unavailable: {Message}", exception.Message);
                }
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Unable to reconcile playback configuration");
            }

            try
            {
                await _reconcileSignal.WaitAsync(interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task<SystemRuntimeSnapshot> ReconcileWithSnapserverAsync(
        CancellationToken cancellationToken)
    {
        PersistentSystemState current = _stateStore.Current;
        SnapServerStatus status = await _snapCastService.GetStatusAsync(cancellationToken);
        var requiredStreams = current.Groups
            .Select(group => (Group: group, StreamId: GetPriorityStreamId(group.SourcePriority)))
            .ToArray();

        var existingStreamIds = status.Streams
            .Select(stream => stream.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach ((PersistentPlaybackGroup group, string streamId) in requiredStreams.DistinctBy(
            entry => entry.StreamId,
            StringComparer.OrdinalIgnoreCase
        ))
        {
            if (existingStreamIds.Contains(streamId))
            {
                continue;
            }
            try
            {
                string sources = string.Join('/', group.SourcePriority);
                string streamUri = $"meta:///{sources}?name={streamId}&codec={_options.Codec}&sampleformat={_options.SampleFormat}";
                await _snapCastService.AddStreamAsync(streamUri, cancellationToken);
                existingStreamIds.Add(streamId);
            }
            catch (Exception exception) when (ContinuesReconcile(exception, cancellationToken))
            {
                _logger.LogWarning(exception, "Unable to create priority stream {StreamId}", streamId);
            }
        }

        var snapClientIdBySpeakerId = current.Speakers.ToDictionary(
            speaker => speaker.SpeakerId!,
            speaker => Identifiers.Normalize(speaker.SnapClientId!),
            StringComparer.OrdinalIgnoreCase
        );
        var onlineClientIds = status.Groups
            .SelectMany(group => group.Clients)
            .Where(client => client.Connected)
            .Select(client => Identifiers.Normalize(client.Id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, SnapGroupStatus> snapGroupByClientId = IndexGroupsByClient(status);

        foreach ((PersistentPlaybackGroup configuredGroup, string streamId) in requiredStreams)
        {
            string[] desiredClientIds = configuredGroup.SpeakerIds
                .Select(speakerId => snapClientIdBySpeakerId[speakerId])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (!desiredClientIds.Any(onlineClientIds.Contains))
            {
                continue;
            }

            try
            {
                SnapGroupStatus? snapGroup = desiredClientIds
                    .Select(clientId => snapGroupByClientId.GetValueOrDefault(clientId))
                    .OfType<SnapGroupStatus>()
                    .GroupBy(group => group.Id, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(candidates => candidates.Count())
                    .Select(candidates => candidates.First())
                    .FirstOrDefault();
                if (snapGroup == null)
                {
                    continue;
                }

                var currentClientIds = snapGroup.Clients
                    .Select(client => Identifiers.Normalize(client.Id))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (!currentClientIds.SetEquals(desiredClientIds))
                {
                    await _snapCastService.SetGroupClientsAsync(
                        snapGroup.Id,
                        desiredClientIds,
                        cancellationToken
                    );
                    status = await _snapCastService.GetStatusAsync(cancellationToken);
                    snapGroupByClientId = IndexGroupsByClient(status);
                    string anchorId = snapGroup.Id;
                    snapGroup = status.Groups.FirstOrDefault(group => group.Id.Equals(
                        anchorId,
                        StringComparison.OrdinalIgnoreCase
                    )) ?? snapGroupByClientId.GetValueOrDefault(desiredClientIds[0]);
                    if (snapGroup == null)
                    {
                        _logger.LogWarning(
                            "Snapcast group for playback group {GroupId} disappeared after regrouping",
                            configuredGroup.Id
                        );
                        continue;
                    }
                }

                if (!snapGroup.Name.Equals(configuredGroup.Name, StringComparison.Ordinal))
                {
                    await _snapCastService.SetGroupNameAsync(
                        snapGroup.Id,
                        configuredGroup.Name,
                        cancellationToken
                    );
                }
                if (!snapGroup.StreamId.Equals(streamId, StringComparison.OrdinalIgnoreCase))
                {
                    await _snapCastService.SetGroupStreamAsync(
                        snapGroup.Id,
                        streamId,
                        cancellationToken
                    );
                }
                if (snapGroup.Muted != configuredGroup.Muted)
                {
                    await _snapCastService.SetGroupMuteAsync(
                        snapGroup.Id,
                        configuredGroup.Muted,
                        cancellationToken
                    );
                }
            }
            catch (Exception exception) when (ContinuesReconcile(exception, cancellationToken))
            {
                _logger.LogWarning(
                    exception,
                    "Unable to reconcile playback group {GroupId}",
                    configuredGroup.Id
                );
            }
        }

        var reportedVolumes = status.Groups
            .SelectMany(group => group.Clients)
            .GroupBy(client => Identifiers.Normalize(client.Id), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                clients => clients.Key,
                clients => clients.First().Config?.Volume,
                StringComparer.OrdinalIgnoreCase
            );
        await _distanceService.ApplyCurrentVolumesAsync(reportedVolumes, cancellationToken,
            status.Streams.Where(stream => stream.Status.Equals("playing", StringComparison.OrdinalIgnoreCase))
                .Select(stream => stream.Id).ToHashSet(StringComparer.OrdinalIgnoreCase));

        HashSet<string> configuredClientIds = current.Speakers
            .Select(speaker => speaker.SnapClientId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (SnapClientStatus client in status.Groups
            .SelectMany(group => group.Clients)
            .Where(client => client.Connected &&
                !configuredClientIds.Contains(client.Id) &&
                client.Config?.Volume?.Percent != 0)
            .DistinctBy(client => client.Id, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                await _snapCastService.SetClientVolumeAsync(client.Id, 0, cancellationToken);
            }
            catch (Exception exception) when (ContinuesReconcile(exception, cancellationToken))
            {
                _logger.LogWarning(
                    exception,
                    "Unable to mute unconfigured Snapclient {SnapClientId}",
                    client.Id
                );
            }
        }

        HashSet<string> requiredStreamIds = requiredStreams
            .Select(entry => entry.StreamId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (SnapStreamStatus stream in status.Streams.Where(stream =>
            stream.Id.StartsWith(_options.StreamPrefix, StringComparison.OrdinalIgnoreCase) &&
            !requiredStreamIds.Contains(stream.Id)
        ))
        {
            try
            {
                await _snapCastService.RemoveStreamAsync(stream.Id, cancellationToken);
            }
            catch (Exception exception) when (ContinuesReconcile(exception, cancellationToken))
            {
                _logger.LogWarning(exception, "Unable to remove stale stream {StreamId}", stream.Id);
            }
        }

        return CreateRuntime(status);
    }

    private static bool ContinuesReconcile(Exception exception, CancellationToken cancellationToken) =>
        exception is not SnapServerUnavailableException &&
        !(exception is OperationCanceledException && cancellationToken.IsCancellationRequested);

    private static Dictionary<string, SnapGroupStatus> IndexGroupsByClient(SnapServerStatus status)
    {
        var groupsByClient = new Dictionary<string, SnapGroupStatus>(StringComparer.OrdinalIgnoreCase);
        foreach (SnapGroupStatus group in status.Groups)
        {
            foreach (SnapClientStatus client in group.Clients)
            {
                groupsByClient.TryAdd(Identifiers.Normalize(client.Id), group);
            }
        }
        return groupsByClient;
    }

    private SystemRuntimeSnapshot CreateRuntime(SnapServerStatus status)
    {
        SnapClientRuntime[] clients = status.Groups
            .SelectMany(group => group.Clients)
            .Where(client => client.Connected)
            .GroupBy(client => client.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Select(client => new SnapClientRuntime
            {
                Id = Identifiers.Normalize(client.Id),
                Name = string.IsNullOrWhiteSpace(client.Host?.Name)
                    ? client.Id
                    : client.Host.Name,
            })
            .OrderBy(client => client.Name)
            .ToArray();
        var streamsById = status.Streams.ToDictionary(
            stream => stream.Id,
            StringComparer.OrdinalIgnoreCase
        );
        AudioSourceRuntime[] sources = _options.Sources.Select(source =>
        {
            bool active = streamsById.TryGetValue(source.Id, out SnapStreamStatus? stream) &&
                stream.Status.Equals("playing", StringComparison.OrdinalIgnoreCase);
            return new AudioSourceRuntime { Id = source.Id, Active = active };
        }).ToArray();
        return new SystemRuntimeSnapshot
        {
            SnapserverOnline = true,
            OnlineSnapClients = clients,
            Sources = sources,
        };
    }

    private SystemRuntimeSnapshot CreateOfflineRuntime() => new()
    {
        SnapserverOnline = false,
        OnlineSnapClients = [],
        Sources = _options.Sources
            .Select(source => new AudioSourceRuntime { Id = source.Id, Active = false })
            .ToArray(),
    };

    private void UpdateRuntime(SystemRuntimeSnapshot runtime)
    {
        if (SameRuntime(CurrentRuntime, runtime))
        {
            return;
        }
        Volatile.Write(ref _currentRuntime, runtime);
        RuntimeChanged?.Invoke();
    }

    private static bool SameRuntime(SystemRuntimeSnapshot left, SystemRuntimeSnapshot right) =>
        left.SnapserverOnline == right.SnapserverOnline &&
        left.OnlineSnapClients.SequenceEqual(right.OnlineSnapClients) &&
        left.Sources.SequenceEqual(right.Sources);

    private async Task<CommandResultMessage> MutateAsync(
        ConfigurationCommand command,
        Func<PersistentSystemState, PersistentSystemState> mutation,
        bool signalReconcile,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.RequestId))
        {
            return Failure(command.RequestId, "requestId is required");
        }
        await _mutationLock.WaitAsync(cancellationToken);
        try
        {
            try
            {
                PersistentSystemState updated = _distanceService.ApplyConfigurationChange(current =>
                {
                    if (command.ExpectedRevision != current.Revision)
                    {
                        throw new ConfigurationRevisionException();
                    }
                    return mutation(current);
                });
                await _distanceService.ApplyCurrentVolumesAsync(reportedVolumes: null, cancellationToken);
                if (signalReconcile)
                {
                    SignalReconcile();
                }
                return new CommandResultMessage
                {
                    RequestId = command.RequestId,
                    Success = true,
                    Revision = updated.Revision,
                };
            }
            catch (ConfigurationRevisionException)
            {
                return Failure(command.RequestId, "Configuration changed; reload and try again");
            }
            catch (Exception exception) when (exception is InvalidOperationException or InvalidDataException)
            {
                return Failure(command.RequestId, exception.Message);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "Unable to apply configuration command {RequestId}",
                    command.RequestId
                );
                return Failure(command.RequestId, "Internal error");
            }
        }
        finally
        {
            _mutationLock.Release();
        }
    }

    internal void SignalReconcile()
    {
        lock (_signalLock)
        {
            if (_reconcileSignal.CurrentCount == 0)
            {
                _reconcileSignal.Release();
            }
        }
    }

    private CommandResultMessage Failure(string requestId, string error) => new()
    {
        RequestId = requestId,
        Success = false,
        Revision = _stateStore.Current.Revision,
        Error = error,
    };

    private void ValidateSpeakerCommand(
        ConfigureSpeakerCommand command,
        PersistentSystemState current)
    {
        if (string.IsNullOrWhiteSpace(command.Name) || string.IsNullOrWhiteSpace(command.SnapClientId))
        {
            throw new InvalidOperationException("Speaker name and Snapclient ID are required");
        }
        if (!double.IsFinite(command.FullVolumeDistance) || command.FullVolumeDistance < 0 ||
            !double.IsFinite(command.MuteDistance) ||
            command.MuteDistance <= command.FullVolumeDistance)
        {
            throw new InvalidOperationException("Speaker distance thresholds are invalid");
        }
        string? speakerId = Identifiers.NormalizeOptional(command.SpeakerId);
        string snapClientId = Identifiers.Normalize(command.SnapClientId);
        string? sensorId = Identifiers.NormalizeOptional(command.SensorId);
        if (current.Speakers.Any(speaker =>
            !speaker.SpeakerId!.Equals(speakerId, StringComparison.OrdinalIgnoreCase) &&
            (speaker.SnapClientId!.Equals(snapClientId, StringComparison.OrdinalIgnoreCase) ||
                (sensorId != null && speaker.SensorId?.Equals(
                    sensorId,
                    StringComparison.OrdinalIgnoreCase
                ) == true))))
        {
            throw new InvalidOperationException("Snapclient and sensor IDs must be unique");
        }
    }

    private void ValidateGroupCommand(
        UpsertGroupCommand command,
        string groupId,
        PersistentSystemState current)
    {
        if (string.IsNullOrWhiteSpace(command.Name) ||
            command.VolumeMode is not ("automatic" or "manual") ||
            !double.IsFinite(command.MasterVolume) ||
            command.MasterVolume is < 0 or > 100)
        {
            throw new InvalidOperationException("Playback group settings are invalid");
        }
        string[] speakerIds = command.SpeakerIds.Select(Identifiers.Normalize).Distinct().ToArray();
        string[] sourceIds = command.SourcePriority.Select(Identifiers.Normalize).Distinct().ToArray();
        if (sourceIds.Length == 0 || sourceIds.Length != command.SourcePriority.Length)
        {
            throw new InvalidOperationException("Choose at least one unique audio source");
        }
        HashSet<string> availableSources = _options.Sources
            .Select(source => source.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (command.SourceLevels is { } levels &&
            (levels.Any(pair => !availableSources.Contains(pair.Key) ||
                !double.IsFinite(pair.Value) || pair.Value is < 0 or > 100) ||
             levels.Keys.Select(Identifiers.Normalize).Distinct().Count() != levels.Count))
        {
            throw new InvalidOperationException("Source levels must be between 0 and 100 for known audio sources");
        }
        if (sourceIds.Any(sourceId => !availableSources.Contains(sourceId)))
        {
            throw new InvalidOperationException("Playback group contains an unknown audio source");
        }
        var speakersById = current.Speakers.ToDictionary(
            speaker => speaker.SpeakerId!,
            StringComparer.OrdinalIgnoreCase
        );
        if (speakerIds.Any(speakerId => !speakersById.ContainsKey(speakerId)))
        {
            throw new InvalidOperationException("Playback group contains an unknown speaker");
        }
        if (current.Groups.Where(group => !group.Id.Equals(
            groupId,
            StringComparison.OrdinalIgnoreCase
        )).SelectMany(group => group.SpeakerIds).Intersect(
            speakerIds,
            StringComparer.OrdinalIgnoreCase
        ).Any())
        {
            throw new InvalidOperationException("A speaker can belong to only one playback group");
        }
        if (command.VolumeMode != "automatic")
        {
            return;
        }

        PersistentPlaybackGroup? existingGroup = current.Groups.FirstOrDefault(group =>
            group.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase)
        );
        bool becomesAutomatic = existingGroup == null || existingGroup.VolumeMode != "automatic";
        IEnumerable<string> speakerIdsToCheck = becomesAutomatic
            ? speakerIds
            : speakerIds.Where(speakerId => !existingGroup!.SpeakerIds.Contains(
                speakerId,
                StringComparer.OrdinalIgnoreCase
            ));
        if (speakerIdsToCheck.Any(speakerId => !PersistentStateFactory.IsCalibrated(speakersById[speakerId])))
        {
            throw new InvalidOperationException("Automatic volume requires calibrated sensors on every speaker");
        }
    }

    private string GetPriorityStreamId(IEnumerable<string> sourcePriority)
    {
        string sourceKey = string.Join('/', sourcePriority.Select(Identifiers.Normalize));
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceKey)))
            .ToLowerInvariant()[..12];
        return $"{_options.StreamPrefix}-{hash}";
    }

    private static SpeakerConfiguration ToConfiguration(PersistentSpeakerState speaker) => new()
    {
        Id = speaker.SpeakerId!,
        Name = speaker.Name!,
        SnapClientId = speaker.SnapClientId!,
        SensorId = speaker.SensorId,
        FullVolumeDistance = speaker.FullVolumeDistance,
        MuteDistance = speaker.MuteDistance,
        Level = speaker.Volume,
        Calibrated = PersistentStateFactory.IsCalibrated(speaker),
    };

    private static PlaybackGroupConfiguration ToConfiguration(PersistentPlaybackGroup group) => new()
    {
        Id = group.Id,
        Name = group.Name,
        SpeakerIds = group.SpeakerIds.ToArray(),
        SourcePriority = group.SourcePriority.ToArray(),
        SourceLevels = new(group.SourceLevels),
        VolumeMode = group.VolumeMode,
        MasterVolume = group.MasterVolume,
        Muted = group.Muted,
    };

    private sealed class ConfigurationRevisionException : Exception
    {
    }
}
