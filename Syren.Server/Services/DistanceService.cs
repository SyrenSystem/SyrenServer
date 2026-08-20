using System.Numerics;
using Microsoft.Extensions.Options;
using Syren.Server.Configuration;
using Syren.Server.Extensions;
using Syren.Server.Models;

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
    private readonly Dictionary<string, Speaker> _speakers;
    private readonly Dictionary<string, SpeakerState> _speakerStates = [];
    private readonly Dictionary<string, double> _lastKnownVolumes = [];
    private readonly HashSet<string> _retiredSensorIds;
    private readonly Dictionary<string, VolumeDispatchState> _volumeDispatchers = [];
    private readonly ISnapCastService _snapCastService;
    private readonly ISystemStateStore _stateStore;
    private readonly ILogger<DistanceService> _logger;
    private int? _lastInsufficientSpeakerCount;

    public DistanceService(
        IOptions<SpeakersOptions> speakersOptions,
        ISnapCastService snapCastService,
        ISystemStateStore stateStore,
        ILogger<DistanceService> logger)
    {
        _snapCastService = snapCastService;
        _stateStore = stateStore;
        _logger = logger;
        _speakers = speakersOptions.Value.SpeakersInfo.ToDictionary(
            speaker => NormalizeId(speaker.SensorId),
            speaker => new Speaker
            {
                SensorId = NormalizeId(speaker.SensorId),
                SnapClientId = NormalizeId(speaker.SnapClientId),
                FullVolumeDistance = speaker.FullVolumeDistance,
                MuteDistance = speaker.MuteDistance,
            },
            StringComparer.OrdinalIgnoreCase
        );

        _retiredSensorIds = new HashSet<string>(
            stateStore.Current.RetiredSensorIds.Select(NormalizeId),
            StringComparer.OrdinalIgnoreCase
        );

        bool stateChanged = false;
        foreach (PersistentSpeakerState persistedSpeaker in stateStore.Current.Speakers)
        {
            string sensorId = NormalizeId(persistedSpeaker.SensorId);
            if (!_speakers.TryGetValue(sensorId, out Speaker speaker))
            {
                _retiredSensorIds.Add(sensorId);
                stateChanged = true;
                continue;
            }

            _lastKnownVolumes[sensorId] = persistedSpeaker.Volume;
            if (persistedSpeaker.Connected && persistedSpeaker.Position.HasValue)
            {
                _speakerStates[sensorId] = new SpeakerState
                {
                    Speaker = speaker,
                    Position = persistedSpeaker.Position.Value.ToVector3(),
                    Distance = 0,
                    Volume = persistedSpeaker.Volume,
                    LastSentVolume = null,
                };
            }
        }

        if (stateChanged)
        {
            SaveStateLocked();
        }
    }

    public string StateId => _stateStore.Current.StateId;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Muting configured Snapclients at startup");
        foreach (Speaker speaker in _speakers.Values)
        {
            try
            {
                await _snapCastService.SetClientVolumeAsync(
                    speaker.SnapClientId,
                    0,
                    cancellationToken
                );
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(
                    exception,
                    "Unable to mute Snapclient {SnapClientId} during startup",
                    speaker.SnapClientId
                );
            }
        }
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
            catch (Exception exception) when (exception is not OperationCanceledException)
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
        string sensorId = NormalizeId(distance.SpeakerId);
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
            targetVolume = ComputeTargetVolume(state);
            if (state.LastSentVolume == targetVolume)
            {
                return Task.CompletedTask;
            }

            snapClientId = state.Speaker.SnapClientId;
        }

        _logger.LogDebug(
            "Distance update for {SensorId} produced Snapcast volume {Volume}",
            sensorId,
            targetVolume
        );
        QueueVolume(sensorId, snapClientId, targetVolume);
        return Task.CompletedTask;
    }

    public Task SetSpeakerVolumeAsync(
        string sensorId,
        double volume,
        CancellationToken cancellationToken = default)
    {
        sensorId = NormalizeId(sensorId);
        string? snapClientId = null;
        int targetVolume = 0;

        lock (_stateLock)
        {
            if (!_speakers.ContainsKey(sensorId))
            {
                _logger.LogWarning("Ignoring volume for unknown speaker {SensorId}", sensorId);
                return Task.CompletedTask;
            }

            bool hadPreviousVolume = _lastKnownVolumes.TryGetValue(
                sensorId,
                out double previousVolume
            );
            _lastKnownVolumes[sensorId] = volume;
            if (_speakerStates.TryGetValue(sensorId, out SpeakerState? state))
            {
                state.Volume = volume;
                snapClientId = state.Speaker.SnapClientId;
                targetVolume = ComputeTargetVolume(state);
            }
            else
            {
                _logger.LogWarning(
                    "Remembering volume for disconnected speaker {SensorId}",
                    sensorId
                );
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
        }

        if (snapClientId != null)
        {
            QueueVolume(sensorId, snapClientId, targetVolume);
        }
        return Task.CompletedTask;
    }

    public async Task<SpeakerState?> ConnectSpeakerAsync(
        string sensorId,
        double volume,
        CancellationToken cancellationToken = default)
    {
        sensorId = NormalizeId(sensorId);
        SpeakerState? state;
        int targetVolume;

        lock (_stateLock)
        {
            if (!_speakers.TryGetValue(sensorId, out Speaker speaker))
            {
                _logger.LogWarning("Ignoring connection for unknown speaker {SensorId}", sensorId);
                return null;
            }

            if (_speakerStates.TryGetValue(sensorId, out state))
            {
                double previousVolume = state.Volume;
                state.Volume = volume;
                _lastKnownVolumes[sensorId] = volume;
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
            else
            {
                bool hadRememberedVolume = _lastKnownVolumes.TryGetValue(
                    sensorId,
                    out double previousRememberedVolume
                );
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
                    Volume = volume,
                    LastSentVolume = null,
                };
                _speakerStates.Add(sensorId, state);
                _lastKnownVolumes[sensorId] = volume;
                try
                {
                    SaveStateLocked();
                }
                catch
                {
                    _speakerStates.Remove(sensorId);
                    if (hadRememberedVolume)
                    {
                        _lastKnownVolumes[sensorId] = previousRememberedVolume;
                    }
                    else
                    {
                        _lastKnownVolumes.Remove(sensorId);
                    }
                    throw;
                }
            }
            targetVolume = ComputeTargetVolume(state);
        }

        await QueueVolumeAndWaitAsync(
            sensorId,
            state.Speaker.SnapClientId,
            targetVolume,
            cancellationToken
        );
        return state;
    }

    public async Task<DisconnectResult> DisconnectSpeakerAsync(
        string sensorId,
        CancellationToken cancellationToken = default)
    {
        sensorId = NormalizeId(sensorId);
        SpeakerState? removedState;

        lock (_stateLock)
        {
            if (!_speakers.ContainsKey(sensorId))
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
        }

        await QueueVolumeAndWaitAsync(
            sensorId,
            removedState.Speaker.SnapClientId,
            0,
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
                    SpeakerId = state.Speaker.SensorId,
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
            string[] normalizedIds = sensorIds.Select(NormalizeId).ToArray();
            string[] removedIds = normalizedIds
                .Where(sensorId => _retiredSensorIds.Remove(sensorId))
                .ToArray();
            if (removedIds.Length != 0)
            {
                try
                {
                    SaveStateLocked();
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

    private int ComputeTargetVolume(SpeakerState state)
    {
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
        return Math.Clamp((int)(state.Volume * modifier), 0, 100);
    }

    private void QueueVolume(string sensorId, string snapClientId, int targetVolume)
    {
        VolumeDispatchState dispatcher = GetDispatcher(sensorId);
        lock (dispatcher.SyncRoot)
        {
            dispatcher.SnapClientId = snapClientId;
            dispatcher.DesiredVolume = targetVolume;
            dispatcher.Version++;
            if (dispatcher.Worker == null || dispatcher.Worker.IsCompleted)
            {
                dispatcher.Worker = RunVolumeDispatcherAsync(sensorId, dispatcher);
            }
        }
    }

    private Task QueueVolumeAndWaitAsync(
        string sensorId,
        string snapClientId,
        int targetVolume,
        CancellationToken cancellationToken)
    {
        VolumeDispatchState dispatcher = GetDispatcher(sensorId);
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (dispatcher.SyncRoot)
        {
            dispatcher.SnapClientId = snapClientId;
            dispatcher.DesiredVolume = targetVolume;
            dispatcher.Version++;
            dispatcher.Waiters.Add((dispatcher.Version, completion));
            if (dispatcher.Worker == null || dispatcher.Worker.IsCompleted)
            {
                dispatcher.Worker = RunVolumeDispatcherAsync(sensorId, dispatcher);
            }
        }
        return completion.Task.WaitAsync(cancellationToken);
    }

    private VolumeDispatchState GetDispatcher(string sensorId)
    {
        lock (_stateLock)
        {
            if (!_volumeDispatchers.TryGetValue(sensorId, out VolumeDispatchState? dispatcher))
            {
                dispatcher = new VolumeDispatchState();
                _volumeDispatchers.Add(sensorId, dispatcher);
            }
            return dispatcher;
        }
    }

    private async Task RunVolumeDispatcherAsync(string sensorId, VolumeDispatchState dispatcher)
    {
        while (true)
        {
            long version;
            int targetVolume;
            string snapClientId;
            lock (dispatcher.SyncRoot)
            {
                version = dispatcher.Version;
                targetVolume = dispatcher.DesiredVolume;
                snapClientId = dispatcher.SnapClientId;
            }

            try
            {
                await _snapCastService.SetClientVolumeAsync(snapClientId, targetVolume);
                lock (_stateLock)
                {
                    if (_speakerStates.TryGetValue(sensorId, out SpeakerState? state))
                    {
                        state.LastSentVolume = targetVolume;
                    }
                }
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

    private void SaveStateLocked()
    {
        var persistentSpeakers = _lastKnownVolumes.Select(entry =>
        {
            bool connected = _speakerStates.TryGetValue(entry.Key, out SpeakerState? state);
            return new PersistentSpeakerState
            {
                SensorId = entry.Key,
                Connected = connected,
                Volume = entry.Value,
                Position = connected ? PositionVector.FromVector3(state!.Position) : null,
            };
        }).ToList();
        _stateStore.Save(new PersistentSystemState
        {
            StateId = _stateStore.Current.StateId,
            Speakers = persistentSpeakers,
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

    private static string NormalizeId(string sensorId) => sensorId.ToLowerInvariant();

    private sealed class VolumeDispatchState
    {
        public object SyncRoot { get; } = new();
        public string SnapClientId { get; set; } = string.Empty;
        public int DesiredVolume { get; set; }
        public long Version { get; set; }
        public Task? Worker { get; set; }
        public List<(long Version, TaskCompletionSource Completion)> Waiters { get; } = [];
    }
}
