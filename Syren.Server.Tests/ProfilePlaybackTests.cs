using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MQTTnet;
using Syren.Server.Handlers;
using Syren.Server.Models;
using Syren.Server.Services;
using Xunit;

namespace Syren.Server.Tests;

public sealed class ProfilePlaybackTests
{
    private readonly TestClock _clock = new();
    private readonly MemoryStateStore _store = new(new PersistentSystemState
    {
        Version = 3,
        StateId = Guid.NewGuid().ToString(),
        Profiles = [new ListenerProfile { Id = "first", Name = "First" }, new ListenerProfile { Id = "second", Name = "Second" }],
    });
    private readonly SessionCatalogueService _catalogue;

    public ProfilePlaybackTests()
    {
        _catalogue = new SessionCatalogueService(_store, _clock);
    }

    private PlaybackSession Session => _store.Current.Sessions.Single();

    [Fact]
    public void PcHeartbeatRestoresTransportAfterAReconcileMessageWasLost()
    {
        _catalogue.BeginGeneration();
        Assert.True(_catalogue.StartPc(PcStart(), _store.Current.Revision));
        _catalogue.BeginGeneration();
        _clock.Advance(4);
        _catalogue.Expire(expireOwners: false);
        Assert.False(Session.Eligible);
        Assert.True(Accepted(PcEvent("heartbeat", 2)));
        Assert.True(Session.Eligible);
        Assert.Equal(1, _store.Current.ClaimSequence);
    }

    [Fact]
    public void SpotifyHeartbeatCannotAcknowledgeAnUndeliveredPlayEvent()
    {
        _catalogue.BeginGeneration();
        Assert.True(Accepted(Event("start", 1)));
        Assert.False(Accepted(Event("heartbeat", 2)));
        Assert.Equal(1, Session.EventSequence);
        Assert.True(Accepted(Event("play", 2)));
        Assert.True(Accepted(Event("heartbeat", 2)));
        Assert.False(Accepted(Event("heartbeat", 3)));
        Assert.Equal(1, _store.Current.ClaimSequence);
    }

    [Fact]
    public void HeartbeatsKeepSessionsEligibleWithoutWritingState()
    {
        _catalogue.BeginGeneration();
        Accepted(Event("start", 1));
        Accepted(Event("play", 2));
        Assert.True(Session.Eligible);
        PersistentSystemState written = _store.Current;
        for (int second = 0; second < 5; second++)
        {
            _clock.Advance(1);
            Assert.True(Accepted(Event("heartbeat", 2)));
            _catalogue.Expire(expireOwners: true);
        }
        Assert.Same(written, _store.Current);
        _clock.Advance(3);
        _catalogue.Expire(expireOwners: true);
        Assert.False(Session.Eligible);
        Assert.Equal(written.CatalogueRevision + 1, _store.Current.CatalogueRevision);
        Assert.True(Accepted(Event("heartbeat", 2)));
        Assert.True(Session.Eligible);
        Assert.Equal(1, _store.Current.ClaimSequence);
    }

    [Fact]
    public void PcClaimRejectsAConfigurationChangedDuringTransportCreation()
    {
        _catalogue.BeginGeneration();
        long revision = _store.Current.Revision;
        _store.Save(_store.Current with { Revision = revision + 1 });
        Assert.False(_catalogue.StartPc(PcStart(), revision));
        Assert.Empty(_store.Current.Sessions);
        Assert.Equal(0, _store.Current.ClaimSequence);
    }

    [Fact]
    public void RawLifecycleMessagesCannotCreateAPcSession()
    {
        _catalogue.BeginGeneration();
        Assert.Equal(LifecycleOutcome.UnknownSession, _catalogue.Apply(PcStart()));
        Assert.Empty(_store.Current.Sessions);
        Assert.Equal(0, _store.Current.ClaimSequence);
    }

    [Fact]
    public void RefusedSpotifyStartIsReportedAsAnUnknownSession()
    {
        _store.Save(_store.Current with
        {
            Groups = [new PersistentPlaybackGroup { Id = "group", Name = "Group", SourcePriority = ["laptop"] }],
        });
        _catalogue.BeginGeneration();
        Assert.Equal(LifecycleOutcome.UnknownSession, _catalogue.Apply(Event("start", 1) with { Destination = "group" }));
        Assert.Equal(LifecycleOutcome.UnknownSession, _catalogue.Apply(Event("play", 2)));
        Assert.Equal(LifecycleOutcome.Ignored, _catalogue.Apply(Event("play", 2) with { Generation = 99 }));
        Assert.Empty(_store.Current.Sessions);
    }

    [Fact]
    public void SpotifyPauseResumeAndDuplicateEventsRespectReleasePolicy()
    {
        _catalogue.BeginGeneration();
        Assert.True(Accepted(Event("start", 1)));
        Assert.True(Accepted(Event("play", 2)));
        long firstClaim = _store.Current.ClaimSequence;
        Assert.False(Accepted(Event("play", 2)));
        Assert.True(Accepted(Event("play", 3)));
        Assert.Equal(firstClaim, _store.Current.ClaimSequence);
        Assert.True(Accepted(Event("pause", 4)));
        Assert.False(Session.Claimed);
        Assert.False(Session.Eligible);
        Assert.True(Accepted(Event("play", 5)));
        Assert.Equal(firstClaim + 1, _store.Current.ClaimSequence);
    }

    [Fact]
    public void ConnectedPolicyRetainsClaimAcrossPause()
    {
        _store.Save(_store.Current with { SourcePolicies = new(_store.Current.SourcePolicies) { ["spotify"] = "connected" } });
        _catalogue.BeginGeneration();
        Accepted(Event("start", 1));
        Accepted(Event("play", 2));
        Accepted(Event("pause", 3));
        Assert.True(Session.Claimed);
        Accepted(Event("play", 4));
        Assert.Equal(1, _store.Current.ClaimSequence);
    }

    [Fact]
    public void TransportRecoveryAndServerRestartDoNotRenewPriority()
    {
        _catalogue.BeginGeneration();
        Accepted(Event("start", 1));
        Accepted(Event("play", 2));
        SessionLifecycleEvent stale = Event("play", 5);
        Accepted(Event("interrupt", 3));
        _clock.Advance(2);
        _catalogue.Expire(expireOwners: true);
        Assert.True(Session.Eligible);
        Accepted(Event("interrupt", 4));
        _clock.Advance(1);
        _catalogue.Expire(expireOwners: true);
        Assert.False(Session.Eligible);
        _catalogue.BeginGeneration();
        Assert.False(Accepted(stale));
        Accepted(Event("reconcile", 5));
        Assert.Equal(1, _store.Current.ClaimSequence);
        Assert.True(Session.Eligible);
    }

    [Fact]
    public void ExplicitlyEndedSessionCannotReturnAfterReconnect()
    {
        _catalogue.BeginGeneration();
        Accepted(Event("start", 1));
        Accepted(Event("play", 2));
        Accepted(Event("end", 3));
        _catalogue.BeginGeneration();
        Assert.False(Accepted(Event("reconcile", 4)));
        Assert.False(Accepted(Event("start", 5)));
        Assert.False(Accepted(Event("play", 6)));
        Assert.Equal("ended", Session.State);
    }

    [Fact]
    public void EndedSessionsAreRemovedAfterTheRetentionPeriod()
    {
        _catalogue.BeginGeneration();
        Accepted(Event("start", 1));
        Accepted(Event("end", 2));
        _catalogue.Expire(expireOwners: true);
        _clock.Advance((int)SessionCatalogueService.EndedRetention.TotalSeconds - 1);
        _catalogue.Expire(expireOwners: true);
        Assert.Equal("ended", Session.State);
        _clock.Advance(1);
        _catalogue.Expire(expireOwners: true);
        Assert.Empty(_store.Current.Sessions);
        Assert.Equal(LifecycleOutcome.UnknownSession, _catalogue.Apply(Event("play", 3)));
    }

    [Fact]
    public void SilentPcOwnsItsClaimUntilItsAppFails()
    {
        _catalogue.BeginGeneration();
        Assert.True(_catalogue.StartPc(PcStart(), _store.Current.Revision));
        _clock.Advance(2);
        _catalogue.Expire(expireOwners: true);
        Assert.True(Session.Claimed);
        Assert.True(Accepted(PcEvent("heartbeat", 1)));
        _clock.Advance(2);
        _catalogue.Expire(expireOwners: true);
        Assert.True(Session.Claimed);
        _clock.Advance(1);
        _catalogue.Expire(expireOwners: true);
        Assert.Equal("ended", Session.State);
        Assert.False(Accepted(PcEvent("heartbeat", 1)));
    }

    [Fact]
    public void LinkingGuestAccountKeepsItsSessionAndClaim()
    {
        _catalogue.BeginGeneration();
        Accepted(Event("start", 1));
        Accepted(Event("play", 2));
        PlaybackSession guest = Session;
        Assert.StartsWith("guest:", guest.OwnerId);
        new ProfileConfigurationService(_store).LinkVerifiedAccount("first", "account-one");
        PlaybackSession linked = Session;
        Assert.Equal("first", linked.OwnerId);
        Assert.Equal(guest.Id, linked.Id);
        Assert.Equal(guest.ClaimSequence, linked.ClaimSequence);
        Assert.Equal(guest.Transports, linked.Transports);
    }

    [Fact]
    public void OnlyTheSelectedReporterCanUpdatePositionAndReadingsExpire()
    {
        PrepareSpeaker();
        var positions = new ProfilePositionService(_store, _clock);
        string firstLease = positions.Select("first", "phone", 1, 0)!;
        Assert.True(positions.Report("first", "phone", firstLease, 1, new Dictionary<string, double> { ["sensor"] = 1000 }, 0));
        Assert.Equal(0.25, positions.DesiredGains()["speaker"]["session"]);
        string replacement = positions.Select("first", "tablet", 1, 0)!;
        Assert.False(positions.Report("first", "phone", firstLease, 2, new Dictionary<string, double> { ["sensor"] = 1000 }, 0));
        Assert.Null(positions.Select("first", "phone", 1, 0));
        Assert.Equal(0, positions.DesiredGains()["speaker"]["session"]);
        positions.Report("first", "tablet", replacement, 1, new Dictionary<string, double> { ["sensor"] = 1000 }, 0);
        _clock.Advance(3);
        Assert.Equal(0, positions.DesiredGains()["speaker"]["session"]);
    }

    [Fact]
    public void SelectionHistoryKeepsOwnersAndStaysBounded()
    {
        var positions = new ProfilePositionService(_store, _clock);
        Assert.NotNull(positions.Select("first", "kept", 1, 0));
        for (int launch = 0; launch < 200; launch++)
        {
            Assert.NotNull(positions.Select("second", "launch-" + launch, 1000 + launch, 0));
        }
        Assert.Equal(64, _store.Current.SelectionSequences.Count);
        Assert.Contains("kept", _store.Current.SelectionSequences.Keys);
        Assert.Contains("launch-199", _store.Current.SelectionSequences.Keys);
        Assert.Null(positions.Select("first", "kept", 1, 0));
    }

    [Fact]
    public void ProfileSwitchClearsPreviousContributionWithoutTransferringPcSession()
    {
        PrepareSpeaker();
        var positions = new ProfilePositionService(_store, _clock);
        string lease = positions.Select("first", "app", 1, 0)!;
        positions.Report("first", "app", lease, 1, new Dictionary<string, double> { ["sensor"] = 1000 }, 0);
        positions.Select("second", "app", 2, 0);
        Assert.Equal(0, positions.DesiredGains()["speaker"]["session"]);
        Assert.Equal("first", Session.OwnerId);
        Assert.False(positions.Report("first", "app", lease, 2, new Dictionary<string, double> { ["sensor"] = 1000 }, 0));
    }

    [Fact]
    public void OverlapRulesAreDisabledByDefaultAndStoredOncePerPair()
    {
        var profile = new ListenerProfile { Id = "first", Name = "First" };
        Assert.False(profile.FollowMe);
        Assert.Empty(profile.Overlap);
        PersistentSystemState state = _store.Current with
        {
            Profiles = [profile with { Overlap = [new SourceOverlap { First = "spotify", Second = "spotify" }] }],
        };
        ProfilePlaybackValidation.Validate(state);
        Assert.Throws<InvalidDataException>(() => ProfilePlaybackValidation.Validate(state with
        {
            Profiles =
            [
                profile with
                {
                    Overlap =
                    [
                        new SourceOverlap { First = "spotify", Second = "laptop" },
                        new SourceOverlap { First = "laptop", Second = "spotify" },
                    ],
                },
            ],
        }));
    }

    [Fact]
    public async Task UnexpectedCommandFailuresStillPublishAResult()
    {
        _catalogue.BeginGeneration();
        var client = new FakeMqttClientService();
        var handler = new ProfilePlaybackHandler(Coordinator(), "Command");
        await handler.HandleMessageAsync(Message(handler.Topic, Command("select", "missing-sequence", new { profileId = "first", instanceId = "app" })), client);
        await handler.HandleMessageAsync(Message(handler.Topic, Command("group", "missing-sources", new { groupId = "room", name = "Room" })), client);
        await handler.CommandsCompleted;
        foreach (string requestId in new[] { "missing-sequence", "missing-sources" })
        {
            JsonElement result = Published(client, "Result/" + requestId);
            Assert.False(result.GetProperty("success").GetBoolean());
            Assert.False(string.IsNullOrWhiteSpace(result.GetProperty("error").GetString()));
        }
    }

    [Fact]
    public async Task LinkingAnAccountUsedByAnotherProfileIsRejectedAndForgotten()
    {
        _catalogue.BeginGeneration();
        new ProfileConfigurationService(_store).LinkVerifiedAccount("first", "account-one");
        var client = new FakeMqttClientService();
        ProfilePlaybackCoordinator coordinator = Coordinator();
        var command = new ProfilePlaybackHandler(coordinator, "Command");
        var linked = new ProfilePlaybackHandler(coordinator, "SpotifyLinked");
        await command.HandleMessageAsync(Message(command.Topic, Command("linkSpotify", "link", new { profileId = "second" })), client);
        await command.CommandsCompleted;
        string ticket = Published(client, "Result/link").GetProperty("linkRequest").GetProperty("ticket").GetString()!;
        string confirmation = JsonSerializer.Serialize(new
        {
            ticket, generation = _store.Current.Generation, stateId = _store.Current.StateId, accountId = "account-one",
        });
        await linked.HandleMessageAsync(Message(linked.Topic, confirmation), client);
        JsonElement status = Published(client, "SpotifyLinkStatus");
        Assert.Equal("rejected", status.GetProperty("status").GetString());
        Assert.Contains("another profile", status.GetProperty("error").GetString());
        Assert.Null(_store.Current.Profiles.Single(profile => profile.Id == "second").SpotifyAccountId);
        Assert.False(coordinator.CompleteSpotifyLink(JsonDocument.Parse(confirmation).RootElement, out _));
    }

    [Fact]
    public async Task LifecycleForAnUnknownSessionTellsTheProducer()
    {
        _catalogue.BeginGeneration();
        var client = new FakeMqttClientService();
        var handler = new ProfilePlaybackHandler(Coordinator(), "Lifecycle");
        await handler.HandleMessageAsync(Message(handler.Topic, JsonSerializer.Serialize(Event("play", 2))), client);
        JsonElement rejected = Published(client, "LifecycleRejected");
        Assert.Equal("session", rejected.GetProperty("sessionId").GetString());
        Assert.Equal("receiver", rejected.GetProperty("producerId").GetString());
        await handler.HandleMessageAsync(Message(handler.Topic, JsonSerializer.Serialize(Event("start", 1))), client);
        Assert.Single(client.Published);
    }

    [Fact]
    public void WallClockStepDoesNotEndLivePcSessions()
    {
        _catalogue.BeginGeneration();
        Assert.True(_catalogue.StartPc(PcStart(), _store.Current.Revision));
        _clock.StepWallClock(60);
        _catalogue.Expire(expireOwners: true);
        Assert.Equal("playing", Session.State);
        Assert.True(Session.Eligible);
        _clock.StepWallClock(-120);
        _clock.Advance(3);
        _catalogue.Expire(expireOwners: true);
        Assert.Equal("ended", Session.State);
    }

    [Fact]
    public void PublishedCatalogueLeavesOutGuestAccountNames()
    {
        _catalogue.BeginGeneration();
        Accepted(Event("start", 1));
        Assert.Equal("account-one", Session.AccountId);
        PlaybackSession published = Assert.Single(_catalogue.Snapshot().Sessions);
        Assert.StartsWith("guest:", published.OwnerId);
        Assert.Null(published.AccountId);
        Assert.DoesNotContain("account-one", JsonSerializer.Serialize(_catalogue.Snapshot()));
    }

    [Fact]
    public async Task ActivationNeedsAtLeastOneSpeaker()
    {
        _catalogue.BeginGeneration();
        var client = new FakeMqttClientService();
        var handler = new ProfilePlaybackHandler(Coordinator(), "Command");
        await handler.HandleMessageAsync(Message(handler.Topic, Command("controllerReady", "ready", new
        {
            capabilities = new[] { "profiles", "source-settings", "position-ownership", "pc-ownership" },
        })), client);
        await handler.HandleMessageAsync(Message(handler.Topic, Command("activate", "activate", new { })), client);
        await handler.CommandsCompleted;
        Assert.True(Published(client, "Result/ready").GetProperty("success").GetBoolean());
        JsonElement result = Published(client, "Result/activate");
        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Contains("at least one speaker", result.GetProperty("error").GetString());
        Assert.False(_store.Current.PlaybackActivated);
    }

    [Fact]
    public async Task SlowCommandsDoNotHoldUpHeartbeats()
    {
        _store.Save(_store.Current with { PlaybackActivated = true });
        _catalogue.BeginGeneration();
        var snapcast = new RecordingSnapCastService { StatusBlocker = new TaskCompletionSource() };
        ProfilePlaybackCoordinator coordinator = Coordinator(snapcast);
        var client = new FakeMqttClientService();
        var command = new ProfilePlaybackHandler(coordinator, "Command");
        var lifecycle = new ProfilePlaybackHandler(coordinator, "Lifecycle");
        await command.HandleMessageAsync(Message(command.Topic, Command("pc", "pc", new { profileId = "first", instanceId = "app", destination = "house" })), client);
        Assert.False(command.CommandsCompleted.IsCompleted);
        await lifecycle.HandleMessageAsync(Message(lifecycle.Topic, JsonSerializer.Serialize(Event("start", 1))), client);
        Assert.Equal("connected", Session.State);
        snapcast.StatusBlocker.SetResult();
        await command.CommandsCompleted.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(Published(client, "Result/pc").GetProperty("success").GetBoolean());
    }

    [Fact]
    public void RepeatedReceiverReportsDoNotChangePublishedState()
    {
        PrepareSpeaker();
        _catalogue.BeginGeneration();
        ProfilePlaybackCoordinator coordinator = Coordinator();
        string Report(long sequence) => JsonSerializer.Serialize(new
        {
            protocolVersion = 3, generation = _store.Current.Generation, speakerId = "speaker", physicalClientId = "client",
            instanceId = "receiver", bootSequence = 1, sequence, ready = true, audible = Array.Empty<string>(),
        });
        Assert.True(coordinator.ReceiverStatus(JsonDocument.Parse(Report(1)).RootElement));
        string first = JsonSerializer.Serialize(coordinator.ReceiverState());
        _clock.Advance(1);
        Assert.True(coordinator.ReceiverStatus(JsonDocument.Parse(Report(2)).RootElement));
        Assert.Equal(first, JsonSerializer.Serialize(coordinator.ReceiverState()));
        Assert.DoesNotContain("sequence", first);
    }

    [Fact]
    public async Task IdlePcReconcileSkipsSnapcastAndDeletesEndedSessionClients()
    {
        PrepareSpeaker();
        string ended = SessionTransportBindings.ClientIdentity("client", "pc-ended");
        string live = SessionTransportBindings.ClientIdentity("client", "transport");
        _store.Save(_store.Current with
        {
            Sessions = [new PlaybackSession { Id = "session", ProducerId = "receiver", OwnerId = "first", Source = "spotify", Destination = "house",
                Transports = [new SessionTransport { Id = "transport", Kind = "snapcast", Endpoint = "stream", Available = true }] }],
        });
        var snapcast = new RecordingSnapCastService
        {
            Status = new Syren.Server.Models.SnapCast.SnapServerStatus
            {
                Groups =
                [
                    new Syren.Server.Models.SnapCast.SnapGroupStatus
                    {
                        Id = "group",
                        Clients =
                        [
                            new Syren.Server.Models.SnapCast.SnapClientStatus { Id = ended, Connected = false },
                            new Syren.Server.Models.SnapCast.SnapClientStatus { Id = live, Connected = false },
                            new Syren.Server.Models.SnapCast.SnapClientStatus { Id = "client", Connected = false },
                        ],
                    },
                ],
            },
        };
        var service = new PcSessionService(_store, _catalogue, snapcast, NullLogger<PcSessionService>.Instance);
        await service.StepAsync(CancellationToken.None);
        Assert.Equal([ended], snapcast.DeletedClients);
        snapcast.Status = new Syren.Server.Models.SnapCast.SnapServerStatus();
        await service.StepAsync(CancellationToken.None);
        int calls = snapcast.StatusCalls;
        await service.StepAsync(CancellationToken.None);
        await service.StepAsync(CancellationToken.None);
        Assert.Equal(calls, snapcast.StatusCalls);
        _store.Save(_store.Current with { Sessions = _store.Current.Sessions.Select(session => session.End()).ToList() });
        await service.StepAsync(CancellationToken.None);
        Assert.Equal(calls + 1, snapcast.StatusCalls);
    }

    private bool Accepted(SessionLifecycleEvent message) => _catalogue.Apply(message) == LifecycleOutcome.Accepted;

    private ProfilePlaybackCoordinator Coordinator(RecordingSnapCastService? snapcast = null)
    {
        snapcast ??= new RecordingSnapCastService();
        return new ProfilePlaybackCoordinator(_store,
            TestServices.CreateConfigurationService(_store, new RecordingDistanceService(_store), snapcast),
            _catalogue, new ProfileConfigurationService(_store), new ProfilePositionService(_store, _clock),
            new PcSessionService(_store, _catalogue, snapcast, NullLogger<PcSessionService>.Instance), _clock);
    }

    private string Command(string action, string requestId, object fields)
    {
        var command = JsonSerializer.SerializeToNode(fields)!.AsObject();
        command["requestId"] = requestId;
        command["protocolVersion"] = 3;
        command["stateId"] = _store.Current.StateId;
        command["generation"] = _store.Current.Generation;
        command["expectedRevision"] = _store.Current.Revision;
        command["action"] = action;
        return command.ToJsonString();
    }

    private static JsonElement Published(FakeMqttClientService client, string suffix) =>
        JsonSerializer.SerializeToElement(client.Published.Last(publish => publish.Topic == ProfilePlaybackHandler.Prefix + suffix).Message);

    private static MqttApplicationMessage Message(string topic, string payload) =>
        new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(payload).Build();

    private void PrepareSpeaker()
    {
        _store.Save(_store.Current with
        {
            Profiles = _store.Current.Profiles.Select(profile => profile with { FollowMe = true }).ToList(),
            Speakers = [new PersistentSpeakerState { SpeakerId = "speaker", Name = "Speaker", SensorId = "sensor", SnapClientId = "client", Volume = 50, Connected = false }],
            Groups = [new PersistentPlaybackGroup { Id = "group", Name = "Group", SpeakerIds = ["speaker"], SourcePriority = ["spotify", "laptop"], MasterVolume = 50 }],
            Sessions = [new PlaybackSession { Id = "session", ProducerId = "app", OwnerId = "first", Source = "laptop", Destination = "house" }],
        });
    }

    private SessionLifecycleEvent Event(string action, long sequence) => new()
    {
        Generation = _store.Current.Generation, SessionId = "session", ProducerId = "receiver", EventSequence = sequence,
        Action = action, Source = "spotify", AccountId = "account-one", Destination = "house",
        Transports = [new SessionTransport { Id = "transport", Kind = "snapcast", Endpoint = "stream", Available = true }],
    };

    private SessionLifecycleEvent PcStart() => Event("start", 1) with { Source = "laptop", OwnerId = "first", AccountId = null };

    private SessionLifecycleEvent PcEvent(string action, long sequence) => Event(action, sequence) with { Source = "laptop" };

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.Parse("2026-09-25T12:00:00Z");
        private long _timestamp;
        public override DateTimeOffset GetUtcNow() => _now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;

        public void Advance(int seconds)
        {
            _now += TimeSpan.FromSeconds(seconds);
            _timestamp += TimeSpan.FromSeconds(seconds).Ticks;
        }

        // Moves only the wall clock, like an NTP correction.
        public void StepWallClock(int seconds) => _now += TimeSpan.FromSeconds(seconds);
    }
}
