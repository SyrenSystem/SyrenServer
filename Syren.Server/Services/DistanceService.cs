using System.Numerics;
using Syren.Server.Extensions;
using Syren.Server.Models;
using Syren.Server.Models.SnapCast;

namespace Syren.Server.Services;

public sealed class DistanceService : IDistanceService, IHostedService
{
    private static readonly Vector3[] Directions =
    [
        Vector3.UnitX,
        Vector3.UnitY,
        Vector3.UnitZ,
        -Vector3.UnitX,
        -Vector3.UnitY,
        -Vector3.UnitZ,
    ];

    private readonly object _stateLock = new();
    private readonly Dictionary<string, Speaker> _speakers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Speaker> _speakersById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PersistentPlaybackGroup> _groupBySpeakerId =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SpeakerState> _speakerStates = [];
    private readonly Dictionary<string, double> _lastKnownVolumes = [];
    private readonly HashSet<string> _retiredSensorIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, VolumeDispatchState> _volumeDispatchers =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ISnapCastService _snapCastService;
    private readonly ISystemStateStore _stateStore;
    private readonly ILogger<DistanceService> _logger;
    private int? _lastInsufficientSpeakerCount;

    public DistanceService(
        ISnapCastService snapCastService,
        ISystemStateStore stateStore,
        ILogger<DistanceService> logger)
    {
        _snapCastService = snapCastService;
        _stateStore = stateStore;
        _logger = logger;

        PersistentSystemState current = stateStore.Current;
        RebuildLookupsLocked(current);
        foreach (PersistentSpeakerState persistedSpeaker in current.Speakers)
        {
            if (persistedSpeaker.SensorId == null)
            {
                continue;
            }
            string sensorId = Identifiers.Normalize(persistedSpeaker.SensorId);
            _lastKnownVolumes[sensorId] = persistedSpeaker.Volume;
            if (persistedSpeaker.Connected && persistedSpeaker.Position.HasValue)
            {
                _speakerStates[sensorId] = new SpeakerState
                {
                    Speaker = _speakers[sensorId],
                    Position = persistedSpeaker.Position.Value.ToVector3(),
                    Distance = 0,
                    Volume = persistedSpeaker.Volume,
                    HasFreshDistance = false,
                };
            }
        }
    }

    public string StateId => _stateStore.Current.StateId;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Muting configured Snapclients at startup");
        Speaker[] configuredSpeakers;
        lock (_stateLock)
        {
            configuredSpeakers = _speakersById.Values.ToArray();
        }

        foreach (Speaker speaker in configuredSpeakers)
        {
            QueueVolume(speaker.SnapClientId, 0);
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Speaker[] activeSpeakers;
        lock (_stateLock)
        {
            activeSpeakers = _speakerStates.Values.Select(state => state.Speaker).ToArray();
        }

        foreach (Speaker speaker in activeSpeakers)
        {
            try
            {
                await _snapCastService.SetClientVolumeAsync(
                    speaker.SnapClientId,
                    0,
                    cancellationToken
                );
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (SnapServerUnavailableException exception)
            {
                _logger.LogWarning(
                    "Unable to mute Snapclient {SnapClientId} during shutdown: {Message}",
                    speaker.SnapClientId,
                    exception.Message
                );
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Unable to mute Snapclient {SnapClientId} during shutdown",
                    speaker.SnapClientId
                );
            }
        }
    }

    public Task UpdateDistanceAsync(
        DistanceData distance,
        CancellationToken cancellationToken = default)
    {
        string sensorId = Identifiers.Normalize(distance.SpeakerId);
        string snapClientId;
        int targetVolume;

        lock (_stateLock)
        {
            if (!_speakers.ContainsKey(sensorId))
            {
                _logger.LogWarning("Ignoring distance for unknown speaker {SensorId}", sensorId);
                return Task.CompletedTask;
            }

            if (!_speakerStates.TryGetValue(sensorId, out SpeakerState? state))
            {
                _logger.LogWarning("Ignoring distance for disconnected speaker {SensorId}", sensorId);
                return Task.CompletedTask;
            }

            state.Distance = distance.Distance;
            state.HasFreshDistance = true;
            targetVolume = ComputeTargetVolumeLocked(state.Speaker.Id, state.Volume, state);
            snapClientId = state.Speaker.SnapClientId;
        }

        _logger.LogDebug(
            "Distance update for {SensorId} produced Snapcast volume {Volume}",
            sensorId,
            targetVolume
        );
        QueueVolume(snapClientId, targetVolume);
        return Task.CompletedTask;
    }

    public Task SetSpeakerVolumeAsync(
        string sensorId,
        double volume,
        CancellationToken cancellationToken = default)
    {
        sensorId = Identifiers.Normalize(sensorId);
        string snapClientId;
        int targetVolume;

        lock (_stateLock)
        {
            if (!_speakers.TryGetValue(sensorId, out Speaker? speaker))
            {
                _logger.LogWarning("Ignoring volume for unknown speaker {SensorId}", sensorId);
                return Task.CompletedTask;
            }

            bool hadPreviousVolume = _lastKnownVolumes.TryGetValue(
                sensorId,
                out double previousVolume
            );
            _lastKnownVolumes[sensorId] = volume;
            _speakerStates.TryGetValue(sensorId, out SpeakerState? state);
            if (state != null)
            {
                state.Volume = volume;
            }

            try
            {
                SaveStateLocked();
            }
            catch
            {
                if (hadPreviousVolume)
                {
                    _lastKnownVolumes[sensorId] = previousVolume;
                }
                else
                {
                    _lastKnownVolumes.Remove(sensorId);
                }
                if (state != null)
                {
                    state.Volume = hadPreviousVolume ? previousVolume : 0;
                }
                throw;
            }

            snapClientId = speaker.SnapClientId;
            targetVolume = ComputeTargetVolumeLocked(speaker.Id, volume, state);
        }

        QueueVolume(snapClientId, targetVolume);
        return Task.CompletedTask;
    }

    public async Task<SpeakerState?> ConnectSpeakerAsync(
        string sensorId,
        double? volume,
        CancellationToken cancellationToken = default)
    {
        sensorId = Identifiers.Normalize(sensorId);
        SpeakerState? state;
        int targetVolume;
        bool inGroup;

        lock (_stateLock)
        {
            if (!_speakers.TryGetValue(sensorId, out Speaker? speaker))
            {
                _logger.LogWarning("Ignoring connection for unknown speaker {SensorId}", sensorId);
                return null;
            }

            bool hadRememberedVolume = _lastKnownVolumes.TryGetValue(
                sensorId,
                out double rememberedVolume
            );
            double level = volume ?? (hadRememberedVolume ? rememberedVolume : 100);
            if (_speakerStates.TryGetValue(sensorId, out state))
            {
                double previousVolume = state.Volume;
                state.Volume = level;
                _lastKnownVolumes[sensorId] = level;
                if (volume.HasValue)
                {
                    try
                    {
                        SaveStateLocked();
                    }
                    catch
                    {
                        state.Volume = previousVolume;
                        _lastKnownVolumes[sensorId] = previousVolume;
                        throw;
                    }
                }
            }
            else
            {
                Vector3? position = GetNewSpeakerPositionLocked();
                if (!position.HasValue || !IsFinite(position.Value))
                {
                    _logger.LogError("Unable to place speaker {SensorId}", sensorId);
                    return null;
                }

                state = new SpeakerState
                {
                    Speaker = speaker,
                    Position = position.Value,
                    Distance = 0,
                    Volume = level,
                    // A fresh connect counts as distance 0 known.
                    HasFreshDistance = true,
                };
                _speakerStates.Add(sensorId, state);
                _lastKnownVolumes[sensorId] = level;
                try
                {
                    SaveStateLocked();
                }
                catch
                {
                    _speakerStates.Remove(sensorId);
                    if (hadRememberedVolume)
                    {
                        _lastKnownVolumes[sensorId] = rememberedVolume;
                    }
                    else
                    {
                        _lastKnownVolumes.Remove(sensorId);
                    }
                    throw;
                }
            }
            inGroup = _groupBySpeakerId.ContainsKey(speaker.Id);
            targetVolume = ComputeTargetVolumeLocked(speaker.Id, state.Volume, state);
        }

        if (!inGroup)
        {
            _logger.LogWarning(
                "Speaker {SensorId} is not in a playback group and stays silent",
                sensorId
            );
        }
        await QueueVolumeAndWaitAsync(state.Speaker.SnapClientId, targetVolume, cancellationToken);
        return state;
    }

    public async Task<DisconnectResult> DisconnectSpeakerAsync(
        string sensorId,
        CancellationToken cancellationToken = default)
    {
        sensorId = Identifiers.Normalize(sensorId);
        SpeakerState? removedState;
        int targetVolume;

        lock (_stateLock)
        {
            if (!_speakers.TryGetValue(sensorId, out Speaker? speaker))
            {
                return DisconnectResult.UnknownSensor;
            }

            if (!_speakerStates.Remove(sensorId, out removedState))
            {
                return DisconnectResult.AlreadyDisconnected;
            }

            try
            {
                SaveStateLocked();
            }
            catch
            {
                _speakerStates[sensorId] = removedState;
                throw;
            }
            targetVolume = ComputeTargetVolumeLocked(speaker.Id, removedState.Volume, null);
        }

        await QueueVolumeAndWaitAsync(
            removedState.Speaker.SnapClientId,
            targetVolume,
            cancellationToken
        );
        return DisconnectResult.Disconnected;
    }

    public Vector3? GetUserPosition()
    {
        lock (_stateLock)
        {
            if (_speakerStates.Count < 3)
            {
                if (_lastInsufficientSpeakerCount != _speakerStates.Count)
                {
                    _logger.LogWarning(
                        "Unable to calculate user position with {SpeakerCount} speakers",
                        _speakerStates.Count
                    );
                    _lastInsufficientSpeakerCount = _speakerStates.Count;
                }
                return null;
            }

            _lastInsufficientSpeakerCount = null;
            Vector3 position = CalculateUserPositionLocked();
            if (!IsFinite(position))
            {
                _logger.LogError("User position calculation produced a nonfinite value");
                return null;
            }
            return position;
        }
    }

    public Task<IReadOnlyList<SpeakerPosition>> GetConnectedSpeakerPositionsAsync(
        CancellationToken cancellationToken = default)
    {
        lock (_stateLock)
        {
            IReadOnlyList<SpeakerPosition> positions = _speakerStates.Values
                .Select(state => new SpeakerPosition
                {
                    SpeakerId = state.Speaker.SensorId!,
                    Position = PositionVector.FromVector3(state.Position),
                })
                .ToArray();
            return Task.FromResult(positions);
        }
    }

    public Task<IReadOnlyList<string>> GetConfiguredSpeakerIdsAsync(
        CancellationToken cancellationToken = default)
    {
        lock (_stateLock)
        {
            return Task.FromResult<IReadOnlyList<string>>(_speakers.Keys.ToArray());
        }
    }

    public Task<IReadOnlyList<string>> GetRetiredSpeakerIdsAsync(
        CancellationToken cancellationToken = default)
    {
        lock (_stateLock)
        {
            return Task.FromResult<IReadOnlyList<string>>(_retiredSensorIds.ToArray());
        }
    }

    public Task ConfirmRetiredSpeakerIdsClearedAsync(
        IEnumerable<string> sensorIds,
        CancellationToken cancellationToken = default)
    {
        lock (_stateLock)
        {
            string[] normalizedIds = sensorIds.Select(Identifiers.Normalize).ToArray();
            string[] removedIds = normalizedIds
                .Where(sensorId => _retiredSensorIds.Remove(sensorId))
                .ToArray();
            if (removedIds.Length != 0)
            {
                try
                {
                    SaveStateLocked(bumpRevision: false);
                }
                catch
                {
                    _retiredSensorIds.UnionWith(removedIds);
                    throw;
                }
            }
        }
        return Task.CompletedTask;
    }

    public PersistentSystemState ApplyConfigurationChange(
        Func<PersistentSystemState, PersistentSystemState> mutation)
    {
        lock (_stateLock)
        {
            PersistentSystemState updated = _stateStore.Update(current =>
                RetireRemovedSensors(current, mutation(current))
            );
            ReloadConfigurationLocked(updated);
            return updated;
        }
    }

    public Task ApplyCurrentVolumesAsync(
        IReadOnlyDictionary<string, SnapClientVolumeStatus?>? reportedVolumes = null,
        CancellationToken cancellationToken = default)
    {
        var changes = new List<(string ClientId, int Volume, SnapClientVolumeStatus? Reported)>();
        lock (_stateLock)
        {
            foreach (PersistentSpeakerState speaker in _stateStore.Current.Speakers)
            {
                string clientId = Identifiers.Normalize(speaker.SnapClientId!);
                SnapClientVolumeStatus? reported = null;
                if (reportedVolumes != null && !reportedVolumes.TryGetValue(clientId, out reported))
                {
                    // Snapserver does not know this client yet, so there is nothing to set.
                    continue;
                }
                string speakerId = Identifiers.Normalize(speaker.SpeakerId!);
                SpeakerState? state = null;
                double level = speaker.Volume;
                if (speaker.SensorId != null)
                {
                    string sensorId = Identifiers.Normalize(speaker.SensorId);
                    _speakerStates.TryGetValue(sensorId, out state);
                    if (_lastKnownVolumes.TryGetValue(sensorId, out double knownLevel))
                    {
                        level = knownLevel;
                    }
                }
                changes.Add((clientId, ComputeTargetVolumeLocked(speakerId, level, state), reported));
            }
        }
        foreach ((string clientId, int volume, SnapClientVolumeStatus? reported) in changes)
        {
            if (reportedVolumes == null)
            {
                QueueVolume(clientId, volume);
            }
            else if (reported != null && reported.Percent == volume && !reported.Muted)
            {
                MarkVolumeSent(clientId, volume);
            }
            else
            {
                QueueVolume(clientId, volume, resend: true);
            }
        }
        return Task.CompletedTask;
    }

    private static PersistentSystemState RetireRemovedSensors(
        PersistentSystemState previous,
        PersistentSystemState updated)
    {
        var remainingSensorIds = updated.Speakers
            .Where(speaker => speaker.SensorId != null)
            .Select(speaker => Identifiers.Normalize(speaker.SensorId!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] removedSensorIds = previous.Speakers
            .Where(speaker => speaker.SensorId != null)
            .Select(speaker => Identifiers.Normalize(speaker.SensorId!))
            .Where(sensorId => !remainingSensorIds.Contains(sensorId))
            .ToArray();
        if (removedSensorIds.Length == 0)
        {
            return updated;
        }
        return updated with
        {
            RetiredSensorIds = updated.RetiredSensorIds
                .Select(Identifiers.Normalize)
                .Concat(removedSensorIds)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order()
                .ToList(),
        };
    }

    private void RebuildLookupsLocked(PersistentSystemState current)
    {
        _speakersById.Clear();
        _speakers.Clear();
        _groupBySpeakerId.Clear();
        _retiredSensorIds.Clear();
        foreach (PersistentSpeakerState persistedSpeaker in current.Speakers)
        {
            Speaker speaker = CreateSpeaker(persistedSpeaker);
            _speakersById.Add(speaker.Id, speaker);
            if (speaker.SensorId != null)
            {
                _speakers.Add(speaker.SensorId, speaker);
            }
        }
        foreach (PersistentPlaybackGroup group in current.Groups)
        {
            foreach (string speakerId in group.SpeakerIds)
            {
                _groupBySpeakerId[Identifiers.Normalize(speakerId)] = group;
            }
        }
        _retiredSensorIds.UnionWith(current.RetiredSensorIds.Select(Identifiers.Normalize));
    }

    private void ReloadConfigurationLocked(PersistentSystemState current)
    {
        RebuildLookupsLocked(current);

        foreach (string sensorId in _speakerStates.Keys
            .Where(sensorId => !_speakers.ContainsKey(sensorId))
            .ToArray())
        {
            _speakerStates.Remove(sensorId);
        }
        foreach (string sensorId in _lastKnownVolumes.Keys
            .Where(sensorId => !_speakers.ContainsKey(sensorId))
            .ToArray())
        {
            _lastKnownVolumes.Remove(sensorId);
        }
        var configuredClientIds = _speakersById.Values
            .Select(speaker => speaker.SnapClientId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string snapClientId in _volumeDispatchers.Keys
            .Where(snapClientId => !configuredClientIds.Contains(snapClientId))
            .ToArray())
        {
            _volumeDispatchers.Remove(snapClientId);
        }

        foreach (PersistentSpeakerState speaker in current.Speakers.Where(
            speaker => speaker.SensorId != null
        ))
        {
            string sensorId = Identifiers.Normalize(speaker.SensorId!);
            _lastKnownVolumes[sensorId] = speaker.Volume;
            if (_speakerStates.TryGetValue(sensorId, out SpeakerState? state))
            {
                state.Speaker = _speakers[sensorId];
                state.Volume = speaker.Volume;
            }
        }
    }

    private Vector3? GetNewSpeakerPositionLocked() => _speakerStates.Count switch
    {
        0 => Vector3.Zero,
        1 => GetSecondSpeakerPositionLocked(),
        2 => GetThirdSpeakerPositionLocked(),
        _ => CalculateUserPositionLocked(),
    };

    private Vector3 GetSecondSpeakerPositionLocked()
    {
        SpeakerState otherSpeaker = _speakerStates.First().Value;
        return otherSpeaker.Position + new Vector3((float)otherSpeaker.Distance, 0, 0);
    }

    private Vector3? GetThirdSpeakerPositionLocked()
    {
        SpeakerState[] speakers = _speakerStates.Values.Take(2).ToArray();
        Vector3 delta = speakers[1].Position - speakers[0].Position;
        float twoSpeakerDistance = delta.Length();
        if (twoSpeakerDistance < 1e-3f)
        {
            _logger.LogError("Cannot place a third speaker when the first two positions coincide");
            return null;
        }

        Vector3 direction = delta / twoSpeakerDistance;
        (Vector3 Near, Vector3 Far)[] extrema =
        [
            (
                speakers[0].Position + direction * (float)speakers[0].Distance,
                speakers[0].Position - direction * (float)speakers[0].Distance
            ),
            (
                speakers[1].Position - direction * (float)speakers[1].Distance,
                speakers[1].Position + direction * (float)speakers[1].Distance
            ),
        ];

        if (twoSpeakerDistance > speakers[0].Distance + speakers[1].Distance)
        {
            return (extrema[0].Near + extrema[1].Near) / 2;
        }
        if (speakers[0].Distance > (speakers[0].Position - extrema[1].Far).Length())
        {
            return (extrema[0].Near + extrema[1].Far) / 2;
        }
        if (speakers[1].Distance > (speakers[1].Position - extrema[0].Far).Length())
        {
            return (extrema[1].Near + extrema[0].Far) / 2;
        }

        double denominator = 2 * speakers[0].Distance * twoSpeakerDistance;
        if (Math.Abs(denominator) < 1e-6)
        {
            return null;
        }
        float cosine = Math.Clamp(
            (float)((
                Math.Pow(speakers[0].Distance, 2) +
                Math.Pow(twoSpeakerDistance, 2) -
                Math.Pow(speakers[1].Distance, 2)
            ) / denominator),
            -1,
            1
        );
        float angle = MathF.Acos(cosine);
        Vector3 axis = GetLeastAlignedAxis(direction);
        Vector3 orthogonal = Vector3.Normalize(Vector3.Cross(direction, axis));
        return speakers[0].Position + (float)speakers[0].Distance * (
            orthogonal * MathF.Sin(angle) + direction * MathF.Cos(angle)
        );
    }

    private Vector3 CalculateUserPositionLocked()
    {
        Sphere[] spheres = _speakerStates.Values.Select(state => new Sphere
        {
            Center = state.Position,
            Radius = state.Distance,
        }).ToArray();
        Vector3 center = _speakerStates.Values.Center();
        return PoorMansGradientDescent(center, position => MeanAbsoluteError(position, spheres));
    }

    private Vector3 PoorMansGradientDescent(Vector3 initialValue, Func<Vector3, double> errorFunction)
    {
        Vector3 result = initialValue;
        double errorValue = errorFunction(result);
        float stepSize = (float)errorValue;
        const double threshold = 0.1;
        const uint maxIterations = 100;

        for (uint iteration = 0; iteration < maxIterations; iteration++)
        {
            if (errorValue <= threshold)
            {
                _logger.LogDebug(
                    "User position {Position} reached error {Error:0.00} in {Iterations} iterations",
                    result,
                    errorValue,
                    iteration
                );
                return result;
            }

            (Vector3 newResult, double newErrorValue) = Directions
                .Select(direction => result + direction * stepSize)
                .Select(position => (position, errorFunction(position)))
                .MinBy(candidate => candidate.Item2);
            if (newErrorValue < errorValue)
            {
                result = newResult;
                errorValue = newErrorValue;
                stepSize = (float)errorValue;
            }
            else
            {
                stepSize /= 2;
            }
        }

        _logger.LogDebug(
            "User position {Position} retained error {Error:0.00} after {Iterations} iterations",
            result,
            errorValue,
            maxIterations
        );
        return result;
    }

    private int ComputeTargetVolumeLocked(string speakerId, double level, SpeakerState? state)
    {
        if (!_groupBySpeakerId.TryGetValue(speakerId, out PersistentPlaybackGroup? group) || group.Muted)
        {
            return 0;
        }

        double groupModifier = group.MasterVolume / 100;
        if (group.VolumeMode == "manual")
        {
            return Math.Clamp((int)Math.Round(level * groupModifier), 0, 100);
        }

        if (state == null || !state.HasFreshDistance)
        {
            return 0;
        }

        double distance = Math.Clamp(
            state.Distance,
            state.Speaker.FullVolumeDistance,
            state.Speaker.MuteDistance
        );
        double modifier = 1 - (
            distance - state.Speaker.FullVolumeDistance
        ) / (
            state.Speaker.MuteDistance - state.Speaker.FullVolumeDistance
        );
        return Math.Clamp((int)Math.Round(level * groupModifier * modifier), 0, 100);
    }

    // Records a volume Snapserver already reports so the dispatcher does not send it again.
    private void MarkVolumeSent(string snapClientId, int volume)
    {
        VolumeDispatchState dispatcher = GetDispatcher(snapClientId);
        lock (dispatcher.SyncRoot)
        {
            if (dispatcher.Worker is { IsCompleted: false })
            {
                return;
            }
            dispatcher.DesiredVolume = volume;
            dispatcher.LastSentVolume = volume;
        }
    }

    // Resend forces the value out even when it matches the last sent volume.
    private void QueueVolume(string snapClientId, int targetVolume, bool resend = false)
    {
        VolumeDispatchState dispatcher = GetDispatcher(snapClientId);
        lock (dispatcher.SyncRoot)
        {
            bool running = dispatcher.Worker is { IsCompleted: false };
            if (resend && !running)
            {
                dispatcher.LastSentVolume = null;
            }
            if (!QueueLocked(dispatcher, targetVolume, running))
            {
                return;
            }
            if (!running)
            {
                dispatcher.Worker = RunVolumeDispatcherAsync(snapClientId, dispatcher);
            }
        }
    }

    private Task QueueVolumeAndWaitAsync(
        string snapClientId,
        int targetVolume,
        CancellationToken cancellationToken)
    {
        VolumeDispatchState dispatcher = GetDispatcher(snapClientId);
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (dispatcher.SyncRoot)
        {
            bool running = dispatcher.Worker is { IsCompleted: false };
            if (!QueueLocked(dispatcher, targetVolume, running) && !running)
            {
                return Task.CompletedTask;
            }
            dispatcher.Waiters.Add((dispatcher.Version, completion));
            if (!running)
            {
                dispatcher.Worker = RunVolumeDispatcherAsync(snapClientId, dispatcher);
            }
        }
        return completion.Task.WaitAsync(cancellationToken);
    }

    // Returns false when the volume is already sent or in flight.
    private static bool QueueLocked(VolumeDispatchState dispatcher, int targetVolume, bool running)
    {
        bool unchanged = running
            ? dispatcher.DesiredVolume == targetVolume
            : dispatcher.LastSentVolume == targetVolume;
        if (unchanged)
        {
            return false;
        }
        dispatcher.DesiredVolume = targetVolume;
        dispatcher.Version++;
        return true;
    }

    private VolumeDispatchState GetDispatcher(string snapClientId)
    {
        lock (_stateLock)
        {
            if (!_volumeDispatchers.TryGetValue(snapClientId, out VolumeDispatchState? dispatcher))
            {
                dispatcher = new VolumeDispatchState();
                _volumeDispatchers.Add(snapClientId, dispatcher);
            }
            return dispatcher;
        }
    }

    private async Task RunVolumeDispatcherAsync(string snapClientId, VolumeDispatchState dispatcher)
    {
        while (true)
        {
            long version;
            int targetVolume;
            lock (dispatcher.SyncRoot)
            {
                version = dispatcher.Version;
                targetVolume = dispatcher.DesiredVolume;
            }

            bool sent = false;
            try
            {
                await _snapCastService.SetClientVolumeAsync(snapClientId, targetVolume);
                sent = true;
            }
            catch (SnapServerUnavailableException exception)
            {
                _logger.LogWarning(
                    "Unable to set Snapclient {SnapClientId} volume to {Volume}: {Message}",
                    snapClientId,
                    targetVolume,
                    exception.Message
                );
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Unable to set Snapclient {SnapClientId} volume to {Volume}",
                    snapClientId,
                    targetVolume
                );
            }

            lock (dispatcher.SyncRoot)
            {
                dispatcher.LastSentVolume = sent ? targetVolume : null;
                foreach ((long waiterVersion, TaskCompletionSource waiter) in
                    dispatcher.Waiters.Where(waiter => waiter.Version <= version).ToArray())
                {
                    waiter.TrySetResult();
                    dispatcher.Waiters.Remove((waiterVersion, waiter));
                }

                if (dispatcher.Version == version)
                {
                    dispatcher.Worker = null;
                    return;
                }
            }
        }
    }

    // Retired sensor bookkeeping is not part of the Configuration snapshot, so it saves without a revision bump.
    private void SaveStateLocked(bool bumpRevision = true)
    {
        _stateStore.Update(current => current with
        {
            Revision = bumpRevision ? current.Revision + 1 : current.Revision,
            Speakers = current.Speakers.Select(existing =>
            {
                string? sensorId = existing.SensorId == null
                    ? null
                    : Identifiers.Normalize(existing.SensorId);
                SpeakerState? state = null;
                bool connected = sensorId != null &&
                    _speakerStates.TryGetValue(sensorId, out state);
                double volume = sensorId != null &&
                    _lastKnownVolumes.TryGetValue(sensorId, out double level)
                    ? level
                    : existing.Volume;
                return existing with
                {
                    Connected = connected,
                    Volume = volume,
                    Position = connected ? PositionVector.FromVector3(state!.Position) : null,
                };
            }).ToList(),
            RetiredSensorIds = _retiredSensorIds.Order().ToList(),
        });
    }

    private static double MeanAbsoluteError(Vector3 position, Sphere[] spheres) =>
        spheres.Sum(sphere => Math.Abs(sphere.Radius - Vector3.Distance(sphere.Center, position))) /
        spheres.Length;

    private static Vector3 GetLeastAlignedAxis(Vector3 direction)
    {
        float x = Math.Abs(Vector3.Dot(direction, Vector3.UnitX));
        float y = Math.Abs(Vector3.Dot(direction, Vector3.UnitY));
        float z = Math.Abs(Vector3.Dot(direction, Vector3.UnitZ));
        return x <= y && x <= z ? Vector3.UnitX : y <= z ? Vector3.UnitY : Vector3.UnitZ;
    }

    private static bool IsFinite(Vector3 position) =>
        float.IsFinite(position.X) &&
        float.IsFinite(position.Y) &&
        float.IsFinite(position.Z);

    private static Speaker CreateSpeaker(PersistentSpeakerState speaker) => new()
    {
        Id = Identifiers.Normalize(speaker.SpeakerId!),
        Name = speaker.Name!,
        SensorId = Identifiers.NormalizeOptional(speaker.SensorId),
        SnapClientId = Identifiers.Normalize(speaker.SnapClientId!),
        FullVolumeDistance = speaker.FullVolumeDistance,
        MuteDistance = speaker.MuteDistance,
    };

    private sealed class VolumeDispatchState
    {
        public object SyncRoot { get; } = new();
        public int DesiredVolume { get; set; }
        public int? LastSentVolume { get; set; }
        public long Version { get; set; }
        public Task? Worker { get; set; }
        public List<(long Version, TaskCompletionSource Completion)> Waiters { get; } = [];
    }
}
