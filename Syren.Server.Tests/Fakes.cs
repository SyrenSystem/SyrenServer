using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MQTTnet.Protocol;
using Syren.Server.Configuration;
using Syren.Server.Models;
using Syren.Server.Models.SnapCast;
using Syren.Server.Services;

namespace Syren.Server.Tests;

internal sealed class MemoryStateStore : ISystemStateStore
{
    public MemoryStateStore(PersistentSystemState? state = null)
    {
        Current = state ?? new PersistentSystemState { StateId = Guid.NewGuid().ToString() };
    }

    public PersistentSystemState Current { get; private set; }
    public bool FailSaves { get; set; }

    public event Action? Changed;

    public void Save(PersistentSystemState state)
    {
        if (FailSaves)
        {
            throw new IOException("State storage failed");
        }
        Current = state;
        Changed?.Invoke();
    }

    public PersistentSystemState Update(Func<PersistentSystemState, PersistentSystemState> update)
    {
        PersistentSystemState state = update(Current);
        Save(state);
        return state;
    }
}

internal sealed class RecordingSnapCastService : ISnapCastService
{
    public List<(string ClientId, int Percent)> VolumeChanges { get; } = [];
    public HashSet<string> FailingClientIds { get; } = [];
    public HashSet<string> FailingGroupIds { get; } = [];
    public HashSet<string> FailingStreamNames { get; } = [];
    public TaskCompletionSource? Blocker { get; set; }
    public SnapServerStatus Status { get; set; } = new();
    public Exception? StatusException { get; set; }
    public int StatusCalls { get; private set; }
    public List<(string GroupId, string[] ClientIds)> GroupClientChanges { get; } = [];
    public List<(string GroupId, string Name)> GroupNameChanges { get; } = [];
    public List<(string GroupId, string StreamId)> GroupStreamChanges { get; } = [];
    public List<(string GroupId, bool Muted)> GroupMuteChanges { get; } = [];
    public List<string> AddedStreams { get; } = [];
    public List<string> RemovedStreams { get; } = [];

    public async Task SetClientVolumeAsync(
        string id,
        int percent,
        CancellationToken cancellationToken = default)
    {
        if (FailingClientIds.Contains(id))
        {
            throw new HttpRequestException("Snapclient unavailable");
        }
        if (Blocker != null)
        {
            await Blocker.Task.WaitAsync(cancellationToken);
        }
        lock (VolumeChanges)
        {
            VolumeChanges.Add((id, percent));
        }
    }

    public Task<SnapServerStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        StatusCalls++;
        if (StatusException != null)
        {
            return Task.FromException<SnapServerStatus>(StatusException);
        }
        return Task.FromResult(Status);
    }

    public Task SetGroupClientsAsync(
        string id,
        IReadOnlyList<string> clientIds,
        CancellationToken cancellationToken = default)
    {
        ThrowIfFailingGroup(id);
        GroupClientChanges.Add((id, clientIds.ToArray()));
        return Task.CompletedTask;
    }

    public Task SetGroupNameAsync(
        string id,
        string name,
        CancellationToken cancellationToken = default)
    {
        ThrowIfFailingGroup(id);
        GroupNameChanges.Add((id, name));
        return Task.CompletedTask;
    }

    public Task SetGroupStreamAsync(
        string id,
        string streamId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfFailingGroup(id);
        GroupStreamChanges.Add((id, streamId));
        return Task.CompletedTask;
    }

    public Task SetGroupMuteAsync(
        string id,
        bool muted,
        CancellationToken cancellationToken = default)
    {
        ThrowIfFailingGroup(id);
        GroupMuteChanges.Add((id, muted));
        return Task.CompletedTask;
    }

    public Task<string> AddStreamAsync(
        string streamUri,
        CancellationToken cancellationToken = default)
    {
        string streamId = StreamName(streamUri);
        if (FailingStreamNames.Contains(streamId))
        {
            throw new InvalidOperationException($"Snapserver rejected stream {streamId}");
        }
        AddedStreams.Add(streamUri);
        return Task.FromResult(streamId);
    }

    public Task RemoveStreamAsync(
        string streamId,
        CancellationToken cancellationToken = default)
    {
        RemovedStreams.Add(streamId);
        return Task.CompletedTask;
    }

    public static string StreamName(string streamUri)
    {
        int nameIndex = streamUri.IndexOf("name=", StringComparison.Ordinal);
        return nameIndex < 0
            ? Guid.NewGuid().ToString()
            : streamUri[(nameIndex + 5)..].Split('&')[0];
    }

    private void ThrowIfFailingGroup(string groupId)
    {
        if (FailingGroupIds.Contains(groupId))
        {
            throw new InvalidOperationException($"Snapserver rejected group {groupId}");
        }
    }
}

internal sealed class RecordingDistanceService : IDistanceService
{
    private readonly MemoryStateStore _stateStore;

    public RecordingDistanceService(MemoryStateStore stateStore)
    {
        _stateStore = stateStore;
    }

    public int ApplyChangeCount { get; private set; }
    public int ApplyVolumeCount { get; private set; }
    public string StateId => _stateStore.Current.StateId;

    public PersistentSystemState ApplyConfigurationChange(
        Func<PersistentSystemState, PersistentSystemState> mutation)
    {
        ApplyChangeCount++;
        return _stateStore.Update(mutation);
    }

    public IReadOnlyDictionary<string, SnapClientVolumeStatus?>? LastReportedVolumes { get; private set; }

    public Task ApplyCurrentVolumesAsync(
        IReadOnlyDictionary<string, SnapClientVolumeStatus?>? reportedVolumes = null,
        CancellationToken cancellationToken = default,
        IReadOnlySet<string>? activeSources = null)
    {
        ApplyVolumeCount++;
        LastReportedVolumes = reportedVolumes;
        return Task.CompletedTask;
    }

    public Task UpdateDistanceAsync(
        DistanceData distance,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task SetSpeakerVolumeAsync(
        string sensorId,
        double volume,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<SpeakerState?> ConnectSpeakerAsync(
        string sensorId,
        double? volume,
        CancellationToken cancellationToken = default) => Task.FromResult<SpeakerState?>(null);

    public Task<DisconnectResult> DisconnectSpeakerAsync(
        string sensorId,
        CancellationToken cancellationToken = default) => Task.FromResult(
        DisconnectResult.UnknownSensor
    );

    public System.Numerics.Vector3? GetUserPosition() => null;

    public Task<IReadOnlyList<SpeakerPosition>> GetConnectedSpeakerPositionsAsync(
        CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SpeakerPosition>>([]);

    public Task<IReadOnlyList<string>> GetConfiguredSpeakerIdsAsync(
        CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);

    public Task<IReadOnlyList<string>> GetRetiredSpeakerIdsAsync(
        CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);

    public Task ConfirmRetiredSpeakerIdsClearedAsync(
        IEnumerable<string> sensorIds,
        CancellationToken cancellationToken = default) => Task.CompletedTask;
}

internal sealed class FakeMqttClientService : IMqttClientService
{
    private readonly object _sync = new();
    private readonly List<(string Topic, object Message, bool Retain, MqttQualityOfServiceLevel Quality)> _published = [];
    private readonly List<string> _cleared = [];
    private int _failuresRemaining;

    public FakeMqttClientService(int failuresBeforeSuccess = 0)
    {
        _failuresRemaining = failuresBeforeSuccess;
    }

    public bool IsConnected { get; private set; }
    public int ConnectionAttempts { get; private set; }

    public IReadOnlyList<(string Topic, object Message, bool Retain, MqttQualityOfServiceLevel Quality)> Published
    {
        get
        {
            lock (_sync)
            {
                return _published.ToArray();
            }
        }
    }

    public IReadOnlyList<string> Cleared
    {
        get
        {
            lock (_sync)
            {
                return _cleared.ToArray();
            }
        }
    }

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ConnectionAttempts++;
        if (_failuresRemaining-- > 0)
        {
            throw new InvalidOperationException("Broker unavailable");
        }
        IsConnected = true;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        IsConnected = false;
        return Task.CompletedTask;
    }

    public Task PublishAsync<T>(
        string topic,
        T message,
        bool retain = false,
        MqttQualityOfServiceLevel qualityOfService = MqttQualityOfServiceLevel.AtLeastOnce,
        CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            _published.Add((topic, message!, retain, qualityOfService));
        }
        return Task.CompletedTask;
    }

    public Task ClearRetainedAsync(string topic, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            _cleared.Add(topic);
        }
        return Task.CompletedTask;
    }

    public Task PublishEmptyAsync(
        string topic,
        bool retain = false,
        MqttQualityOfServiceLevel qualityOfService = MqttQualityOfServiceLevel.AtMostOnce,
        CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            _cleared.Add(topic);
        }
        return Task.CompletedTask;
    }
}

internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send;

    public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
    {
        _send = (request, cancellationToken) => Task.FromResult(responseFactory(request));
    }

    public StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
    {
        _send = send;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) => _send(request, cancellationToken);

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };
}

internal static class TestServices
{
    public static readonly string[] SourceIds = ["spotify", "laptop"];

    public static IOptions<PlaybackOptions> CreatePlaybackOptions(int reconcileSeconds = 5) => Options.Create(
        new PlaybackOptions
        {
            Sources =
            [
                new AudioSourceOption { Id = "spotify", Name = "Spotify" },
                new AudioSourceOption { Id = "laptop", Name = "Laptop audio" },
            ],
            ReconcileSeconds = reconcileSeconds,
        }
    );

    public static DistanceService CreateDistanceService(
        ISnapCastService snapCastService,
        MemoryStateStore? stateStore = null,
        params SpeakerInfo[] speakers)
    {
        if (speakers.Length == 0)
        {
            speakers =
            [
                new SpeakerInfo
                {
                    SensorId = "sensor",
                    SnapClientId = "snap-client",
                    FullVolumeDistance = 0,
                    MuteDistance = 100,
                },
            ];
        }
        stateStore ??= new MemoryStateStore();
        PersistentSystemState state = PersistentStateFactory.SeedConfiguredSpeakers(
            stateStore.Current,
            new SpeakersOptions { SpeakersInfo = speakers }
        );
        if (state.Groups.Count == 0)
        {
            state = state with
            {
                Groups =
                [
                    PersistentStateFactory.CreateDefaultGroup(
                        state.Speakers.Select(speaker => speaker.SpeakerId!),
                        SourceIds,
                        "automatic"
                    ),
                ],
            };
        }
        if (!ReferenceEquals(state, stateStore.Current))
        {
            stateStore.Save(state);
        }
        return new DistanceService(
            snapCastService,
            stateStore,
            NullLogger<DistanceService>.Instance
        );
    }

    public static SystemConfigurationService CreateConfigurationService(
        MemoryStateStore stateStore,
        IDistanceService distanceService,
        RecordingSnapCastService snapCastService,
        int reconcileSeconds = 5) => new(
        stateStore,
        distanceService,
        snapCastService,
        CreatePlaybackOptions(reconcileSeconds),
        NullLogger<SystemConfigurationService>.Instance
    );

    public static async Task WaitUntilAsync(Func<bool> condition, int timeoutMilliseconds = 2000)
    {
        using var cancellationTokenSource = new CancellationTokenSource(timeoutMilliseconds);
        while (!condition())
        {
            await Task.Delay(10, cancellationTokenSource.Token);
        }
    }
}
