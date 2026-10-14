using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    private readonly CastingDeviceRegistry _registry;
    private readonly SessionCatalogueService _catalogue;
    private readonly ProducerVolumeStore _volumes;

    public ProfilePlaybackTests()
    {
        _registry = new CastingDeviceRegistry(_store, _clock);
        _catalogue = new SessionCatalogueService(_store, _clock, _registry);
        _volumes = new ProducerVolumeStore(null, _clock);
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
        new ProfileConfigurationService(_store, _registry).LinkVerifiedAccount("first", "account-one");
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
        var positions = new ProfilePositionService(_store, _clock, _volumes);
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
        var positions = new ProfilePositionService(_store, _clock, _volumes);
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
        var positions = new ProfilePositionService(_store, _clock, _volumes);
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
        new ProfileConfigurationService(_store, _registry).LinkVerifiedAccount("first", "account-one");
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

    [Fact]
    public async Task DesktopOutputsKeepWarmCopiesAndPreserveTheClaim()
    {
        PrepareSpeaker();
        _store.Save(_store.Current with { PlaybackActivated = true, Sessions = [] });
        _catalogue.BeginGeneration();
        var snapcast = new RecordingSnapCastService();
        var service = new PcSessionService(_store, _catalogue, snapcast, NullLogger<PcSessionService>.Instance);
        for (int index = 0; index < 2; index++)
        {
            await service.StartAsync("request", "first", "app" + index, "house", "speaker", "192.168.1.2", "192.168.1.3",
                _store.Current.Revision, _store.Current.Generation, CancellationToken.None, desktopOutputs: true);
        }
        Assert.Equal(2, _store.Current.ClaimSequence);
        Assert.Equal(2, snapcast.AddedStreams.Count);
        Assert.Equal([4954, 4955], _store.Current.Sessions.SelectMany(session => session.Transports)
            .Where(transport => transport.TcpPort != null).Select(transport => transport.TcpPort!.Value).ToArray());
        foreach (PlaybackSession session in _store.Current.Sessions)
        {
            Assert.Equal(2, session.Transports.Count);
            Assert.Equal("auto", session.PcMode);
            Assert.Equal(["snapcast", "rtp"], session.Transports.Select(transport => transport.Kind).ToArray());
            SessionTransport[] original = session.Transports.ToArray();
            for (int index = 0; index < 20; index++)
            {
                string mode = new[] { "stable", "fast", "auto" }[index % 3];
                Assert.Equal(LifecycleOutcome.Accepted, _catalogue.Apply(new SessionLifecycleEvent
                {
                    Generation = _store.Current.Generation, SessionId = session.Id, ProducerId = session.ProducerId,
                    EventSequence = index + 2, Action = "transport", PcMode = mode,
                }));
                PlaybackSession updated = _store.Current.Sessions.Single(candidate => candidate.Id == session.Id);
                Assert.Equal(mode, updated.PcMode);
                Assert.Equal(session.ClaimSequence, updated.ClaimSequence);
                Assert.Equal(original, updated.Transports);
            }
        }
        Assert.Equal(2, _store.Current.Sessions.SelectMany(session => session.Transports)
            .Where(transport => transport.Kind == "rtp").Select(transport => transport.Endpoint).Distinct().Count());
    }

    [Fact]
    public async Task DesktopOutputsRejectReceiversWithoutTheCapability()
    {
        PrepareSpeaker();
        _store.Save(_store.Current with { PlaybackActivated = true, Sessions = [] });
        _catalogue.BeginGeneration();
        var snapcast = new RecordingSnapCastService();
        ProfilePlaybackCoordinator coordinator = Coordinator(snapcast);
        var client = new FakeMqttClientService();
        var handler = new ProfilePlaybackHandler(coordinator, "Command");
        await handler.HandleMessageAsync(Message(handler.Topic, Command("pc", "pc", new
        {
            profileId = "first", instanceId = "app", destination = "house", desktopOutputs = true,
        })), client);
        await handler.CommandsCompleted;
        Assert.False(Published(client, "Result/pc").GetProperty("success").GetBoolean());
        Assert.Empty(snapcast.AddedStreams);
        Assert.Empty(_store.Current.Sessions);
    }

    [Theory]
    [InlineData("fast")]
    [InlineData("unknown")]
    public void InvalidDesktopModesAreRejected(string mode)
    {
        _catalogue.BeginGeneration();
        Assert.False(_catalogue.StartPc(PcStart() with { PcMode = mode }, _store.Current.Revision));
    }

    [Fact]
    public void CastingStartClaimsButWaitsForPlay()
    {
        _catalogue.BeginGeneration();
        Assert.True(Accepted(CastingEvent("start", 1)));
        Assert.True(Session.Claimed);
        Assert.Equal("connected", Session.State);
        Assert.False(Session.Eligible);
        Assert.Equal(1, Session.ClaimSequence);
        Assert.True(Accepted(CastingEvent("play", 2)));
        Assert.Equal("playing", Session.State);
        Assert.True(Session.Eligible);
        Assert.Equal(1, Session.ClaimSequence);
        Assert.Equal(1, _store.Current.ClaimSequence);
    }

    [Fact]
    public void CastingPauseKeepsClaimAndResumeDoesNotRenew()
    {
        _catalogue.BeginGeneration();
        Accepted(CastingEvent("start", 1));
        Accepted(CastingEvent("play", 2));
        Assert.True(Accepted(CastingEvent("pause", 3)));
        Assert.Equal("paused", Session.State);
        Assert.True(Session.Claimed);
        Assert.False(Session.Eligible);
        Assert.True(Accepted(CastingEvent("play", 4)));
        Assert.True(Session.Eligible);
        Assert.Equal(1, Session.ClaimSequence);
        Assert.Equal(1, _store.Current.ClaimSequence);
    }

    [Fact]
    public void CastingHeartbeatCannotAcknowledgeAnUndeliveredEvent()
    {
        _catalogue.BeginGeneration();
        Assert.True(Accepted(CastingEvent("start", 1)));
        Assert.False(Accepted(CastingEvent("heartbeat", 2)));
        Assert.Equal(1, Session.EventSequence);
        Assert.False(Accepted(CastingEvent("play", 3)));
        Assert.True(Accepted(CastingEvent("play", 2)));
        Assert.True(Accepted(CastingEvent("heartbeat", 2)));
        Assert.False(Accepted(CastingEvent("heartbeat", 1)));
        Assert.False(Accepted(CastingEvent("heartbeat", 3)));
        Assert.Equal(2, Session.EventSequence);
    }

    [Fact]
    public void CastingNeedsHeartbeatsAndEndsAfterThirtySilentSeconds()
    {
        _catalogue.BeginGeneration();
        Accepted(CastingEvent("start", 1));
        Accepted(CastingEvent("play", 2));
        Assert.True(Session.Eligible);
        _clock.Advance(3);
        _catalogue.Expire(expireOwners: true);
        Assert.False(Session.Eligible);
        Assert.Equal("playing", Session.State);
        Assert.True(Accepted(CastingEvent("heartbeat", 2)));
        Assert.True(Session.Eligible);
        _clock.Advance(29);
        _catalogue.Expire(expireOwners: true);
        Assert.Equal("playing", Session.State);
        Assert.True(Session.Claimed);
        _clock.Advance(1);
        _catalogue.Expire(expireOwners: true);
        Assert.Equal("ended", Session.State);
        Assert.False(Accepted(CastingEvent("heartbeat", 2)));
    }

    [Fact]
    public async Task CastingStartThatIsStillSavingKeepsItsHeartbeat()
    {
        var store = new PausingSaveStore(_store.Current);
        var catalogue = new SessionCatalogueService(store, _clock, new CastingDeviceRegistry(store, _clock));
        catalogue.BeginGeneration();
        using var release = new ManualResetEventSlim();
        Task<LifecycleOutcome>? start = null;
        // The MQTT thread applies a start right after Expire's update, and its save is still running when Expire tidies up.
        store.AfterUpdate = () =>
        {
            store.HoldSave = release;
            start = Task.Run(() => catalogue.Apply(CastingEvent("start", 1) with { Generation = store.Current.Generation }));
            Assert.True(store.Saving.Wait(TimeSpan.FromSeconds(5)));
        };
        await Task.Run(() => catalogue.Expire(expireOwners: true));
        release.Set();
        Assert.Equal(LifecycleOutcome.Accepted, await start!);
        catalogue.Expire(expireOwners: true);
        Assert.Equal("connected", store.Current.Sessions.Single().State);
    }

    [Fact]
    public async Task CastingIsNotEndedDuringTheGraceAfterAnMqttReconnect()
    {
        var mqtt = new FakeMqttClientService { ConnectionEpoch = 1 };
        mqtt.SetConnected(true);
        ProfilePlaybackCoordinator coordinator = Coordinator();
        var bindings = new SessionTransportBindings(coordinator, _store, new RecordingSnapCastService(),
            NullLogger<SessionTransportBindings>.Instance);
        using var publisher = new ProfilePlaybackPublisher(coordinator, _catalogue, bindings, _store, _volumes, mqtt, _clock,
            NullLogger<ProfilePlaybackPublisher>.Instance);
        _catalogue.BeginGeneration();
        Accepted(CastingEvent("start", 1));
        Accepted(CastingEvent("play", 2));
        await publisher.StartAsync(CancellationToken.None);
        try
        {
            await PublisherPasses(mqtt);
            Assert.Contains(mqtt.Published, publish => publish.Topic == ProfilePlaybackHandler.Prefix + "CastingDevices" && publish.Retain);
            mqtt.SetConnected(false);
            await PublisherPasses(mqtt);
            _clock.Advance(60);
            await PublisherPasses(mqtt);
            mqtt.ConnectionEpoch = 2;
            mqtt.SetConnected(true);
            await PublisherPasses(mqtt);
            Assert.Equal("playing", Session.State);
            _clock.Advance(3);
            await PublisherPasses(mqtt);
            Assert.Equal("playing", Session.State);
            _clock.Advance(27);
            await TestServices.WaitUntilAsync(() => Session.State == "ended");
        }
        finally
        {
            await publisher.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void CastingOwnerComesFromTheAccountNotTheOwnerField()
    {
        _catalogue.BeginGeneration();
        Assert.True(Accepted(CastingEvent("start", 1) with { OwnerId = "first" }));
        string deviceId = CastingDevice("airplay:device:aa");
        Assert.Matches("^[0-9a-f]{64}$", deviceId);
        Assert.Equal("guest:" + deviceId, Session.OwnerId);
        Assert.Equal(deviceId, Session.SenderId);
        Assert.Equal("airplay", Session.Protocol);
        Assert.Equal("airplay:device:aa", Session.AccountId);
    }

    [Fact]
    public void CastingIdsAreKeyedAndTheKeyIsNeverPublished()
    {
        byte[] firstKey = RandomNumberGenerator.GetBytes(32);
        byte[] secondKey = RandomNumberGenerator.GetBytes(32);
        Assert.Equal(SessionCatalogueService.CastingDeviceId(firstKey, "airplay:device:aa"),
            SessionCatalogueService.CastingDeviceId(firstKey, "airplay:device:aa"));
        Assert.NotEqual(SessionCatalogueService.CastingDeviceId(firstKey, "airplay:device:aa"),
            SessionCatalogueService.CastingDeviceId(secondKey, "airplay:device:aa"));
        _catalogue.BeginGeneration();
        Assert.Null(_store.Current.CastingKey);
        Accepted(CastingEvent("start", 1));
        Accepted(CastingEvent("play", 2));
        string key = _store.Current.CastingKey!;
        Assert.Equal(32, Convert.FromBase64String(key).Length);
        Accepted(CastingEvent("end", 3));
        Accepted(CastingEvent("start", 1) with { SessionId = "second" });
        Assert.Equal(key, _store.Current.CastingKey);
        string plainHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("airplay:device:aa")));
        ProfilePlaybackCoordinator coordinator = Coordinator();
        object[] payloads = [coordinator.Configuration(), _catalogue.Snapshot(), coordinator.CastingDevices(), coordinator.Gains(),
            coordinator.ReceiverState()];
        foreach (string published in payloads.Select(payload => JsonSerializer.Serialize(payload)))
        {
            Assert.DoesNotContain(key, published);
            Assert.DoesNotContain(JsonSerializer.Serialize(key)[1..^1], published);
            Assert.DoesNotContain(plainHash, published);
            Assert.DoesNotContain("airplay:device:aa", published);
        }
        Assert.Contains(CastingDevice("airplay:device:aa"), JsonSerializer.Serialize(_catalogue.Snapshot()));
    }

    [Fact]
    public void EndThenStartFromTheSameProducerTakesANewerClaim()
    {
        _catalogue.BeginGeneration();
        Accepted(CastingEvent("start", 1));
        Accepted(CastingEvent("play", 2));
        Assert.True(Accepted(CastingEvent("end", 3)));
        Assert.True(Accepted(CastingEvent("start", 1) with { SessionId = "next" }));
        PlaybackSession ended = _store.Current.Sessions.Single(session => session.Id == "session");
        PlaybackSession next = _store.Current.Sessions.Single(session => session.Id == "next");
        Assert.Equal("ended", ended.State);
        Assert.False(ended.Claimed);
        Assert.True(next.Claimed);
        Assert.Equal(2, next.ClaimSequence);
        Assert.Equal(ended.SenderId, next.SenderId);
        Assert.Equal(ended.OwnerId, next.OwnerId);
        Assert.False(Accepted(CastingEvent("start", 4)));
    }

    [Fact]
    public async Task LinkedCastingDeviceOwnsNewSessions()
    {
        _catalogue.BeginGeneration();
        Accepted(CastingEvent("start", 1));
        Accepted(CastingEvent("end", 2));
        string deviceId = CastingDevice("airplay:device:aa");
        Assert.True(Succeeded(await RunCommand(Coordinator(), "linkCasting", "link", new { profileId = "second", deviceId })));
        Assert.True(Accepted(CastingEvent("start", 1) with { SessionId = "linked", OwnerId = "first" }));
        PlaybackSession linked = _store.Current.Sessions.Single(session => session.Id == "linked");
        Assert.Equal("second", linked.OwnerId);
        Assert.Equal(deviceId, linked.SenderId);
        Assert.True(Accepted(CastingEvent("start", 1) with { SessionId = "other", AccountId = "airplay:device:bb" }));
        Assert.StartsWith("guest:", _store.Current.Sessions.Single(session => session.Id == "other").OwnerId);
    }

    [Fact]
    public async Task LinkingAPlayingCastingGuestKeepsSessionClaimAndTransports()
    {
        _catalogue.BeginGeneration();
        Accepted(CastingEvent("start", 1));
        Accepted(CastingEvent("play", 2));
        PlaybackSession guest = Session;
        Assert.StartsWith("guest:", guest.OwnerId);
        long revision = _store.Current.Revision;
        long catalogueRevision = _store.Current.CatalogueRevision;
        ProfilePlaybackCoordinator coordinator = Coordinator();
        Assert.True(Succeeded(await RunCommand(coordinator, "linkCasting", "link", new { profileId = "first", deviceId = guest.SenderId })));
        PlaybackSession linked = Session;
        Assert.Equal("first", linked.OwnerId);
        Assert.Equal(guest.Id, linked.Id);
        Assert.Equal(guest.ClaimSequence, linked.ClaimSequence);
        Assert.True(linked.Claimed);
        Assert.Equal(guest.Transports, linked.Transports);
        Assert.Equal("playing", linked.State);
        Assert.True(linked.Eligible);
        Assert.Equal(revision + 1, _store.Current.Revision);
        Assert.Equal(catalogueRevision + 1, _store.Current.CatalogueRevision);
        Assert.Equal(new LinkedCastingDevice { Id = guest.SenderId!, Name = "Phone" },
            Assert.Single(_store.Current.Profiles.Single(profile => profile.Id == "first").CastingDevices));
        JsonElement device = JsonSerializer.SerializeToElement(coordinator.CastingDevices()).GetProperty("devices")[0];
        Assert.Equal("first", device.GetProperty("profileId").GetString());
        Assert.Equal("first", device.GetProperty("ownerId").GetString());
        Assert.True(device.GetProperty("playing").GetBoolean());
        Assert.True(Succeeded(await RunCommand(coordinator, "linkCasting", "again", new { profileId = "first", deviceId = guest.SenderId })));
        Assert.Single(_store.Current.Profiles.Single(profile => profile.Id == "first").CastingDevices);
        Assert.Equal(guest.ClaimSequence, Session.ClaimSequence);
        Assert.Equal(revision + 1, _store.Current.Revision);
    }

    [Fact]
    public async Task LinkCastingWithAStaleRevisionIsRejected()
    {
        _catalogue.BeginGeneration();
        Accepted(CastingEvent("start", 1));
        string deviceId = Session.SenderId!;
        long stale = _store.Current.Revision;
        string command = Command("linkCasting", "stale", new { profileId = "first", deviceId });
        _store.Save(_store.Current with { Revision = stale + 1 });
        var client = new FakeMqttClientService();
        var handler = new ProfilePlaybackHandler(Coordinator(), "Command");
        await handler.HandleMessageAsync(Message(handler.Topic, command), client);
        await handler.CommandsCompleted;
        Assert.False(Succeeded(Published(client, "Result/stale")));
        CommandResultMessage direct = new ProfileConfigurationService(_store, _registry).Configure(new ProfileConfigurationCommand
        {
            RequestId = "direct", ExpectedRevision = stale, ProtocolVersion = 3, StateId = _store.Current.StateId,
            Generation = _store.Current.Generation, Action = "linkCasting", ProfileId = "first", DeviceId = deviceId,
        });
        Assert.False(direct.Success);
        Assert.Empty(_store.Current.Profiles.Single(profile => profile.Id == "first").CastingDevices);
        Assert.Equal("guest:" + deviceId, Session.OwnerId);
        Assert.Equal(stale + 1, _store.Current.Revision);
    }

    [Fact]
    public async Task UnlinkingCastingReturnsSessionToItsGuestWithoutEnding()
    {
        _catalogue.BeginGeneration();
        Accepted(CastingEvent("start", 1));
        Accepted(CastingEvent("play", 2));
        PlaybackSession guest = Session;
        ProfilePlaybackCoordinator coordinator = Coordinator();
        Assert.True(Succeeded(await RunCommand(coordinator, "linkCasting", "link", new { profileId = "first", deviceId = guest.SenderId })));
        Assert.Equal("first", Session.OwnerId);
        Assert.True(Succeeded(await RunCommand(coordinator, "unlinkCasting", "unlink", new { profileId = "first", deviceId = guest.SenderId })));
        PlaybackSession unlinked = Session;
        Assert.Equal(guest.OwnerId, unlinked.OwnerId);
        Assert.Equal(guest.Id, unlinked.Id);
        Assert.Equal("playing", unlinked.State);
        Assert.True(unlinked.Claimed);
        Assert.Equal(guest.ClaimSequence, unlinked.ClaimSequence);
        Assert.Equal(guest.Transports, unlinked.Transports);
        Assert.Empty(_store.Current.Profiles.Single(profile => profile.Id == "first").CastingDevices);
        JsonElement device = JsonSerializer.SerializeToElement(coordinator.CastingDevices()).GetProperty("devices")[0];
        Assert.Equal(JsonValueKind.Null, device.GetProperty("profileId").ValueKind);
        Assert.Equal(guest.OwnerId, device.GetProperty("ownerId").GetString());
    }

    [Fact]
    public async Task LinkingACastingDeviceOfAnotherProfileIsRejected()
    {
        _catalogue.BeginGeneration();
        Accepted(CastingEvent("start", 1));
        string deviceId = Session.SenderId!;
        ProfilePlaybackCoordinator coordinator = Coordinator();
        Assert.True(Succeeded(await RunCommand(coordinator, "linkCasting", "first", new { profileId = "first", deviceId })));
        JsonElement rejected = await RunCommand(coordinator, "linkCasting", "second", new { profileId = "second", deviceId });
        Assert.False(Succeeded(rejected));
        Assert.Equal("This device is already linked to another profile", rejected.GetProperty("error").GetString());
        Assert.Empty(_store.Current.Profiles.Single(profile => profile.Id == "second").CastingDevices);
        Assert.Equal("first", Session.OwnerId);
        JsonElement missing = await RunCommand(coordinator, "linkCasting", "missing", new { profileId = "missing", deviceId });
        Assert.Equal("This device has not played here in the last 15 minutes", missing.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ForgettingALinkedDeviceIsRejected()
    {
        _catalogue.BeginGeneration();
        Accepted(CastingEvent("start", 1));
        Accepted(CastingEvent("end", 2));
        string deviceId = CastingDevice("airplay:device:aa");
        ProfilePlaybackCoordinator coordinator = Coordinator();
        Assert.True(Succeeded(await RunCommand(coordinator, "linkCasting", "link", new { profileId = "first", deviceId })));
        JsonElement rejected = await RunCommand(coordinator, "forgetCasting", "forget", new { deviceId });
        Assert.False(Succeeded(rejected));
        Assert.Equal("Unlink this device first", rejected.GetProperty("error").GetString());
        Assert.NotNull(_registry.Find(deviceId));
        Assert.True(Succeeded(await RunCommand(coordinator, "unlinkCasting", "unlink", new { profileId = "first", deviceId })));
        long revision = _store.Current.Revision;
        Assert.True(Succeeded(await RunCommand(coordinator, "forgetCasting", "forgotten", new { deviceId })));
        Assert.Equal(revision, _store.Current.Revision);
        Assert.Null(_registry.Find(deviceId));
        Assert.Empty(JsonSerializer.SerializeToElement(coordinator.CastingDevices()).GetProperty("devices").EnumerateArray());
        JsonElement relink = await RunCommand(coordinator, "linkCasting", "relink", new { profileId = "first", deviceId });
        Assert.Equal("This device has not played here in the last 15 minutes", relink.GetProperty("error").GetString());
    }

    [Fact]
    public async Task CastingCommandsThatChangeNothingNeverMakeAnotherAppStale()
    {
        _catalogue.BeginGeneration();
        Accepted(CastingEvent("start", 1));
        Accepted(CastingEvent("end", 2));
        string deviceId = CastingDevice("airplay:device:aa");
        ProfilePlaybackCoordinator coordinator = Coordinator();
        Assert.True(Succeeded(await RunCommand(coordinator, "linkCasting", "link", new { profileId = "first", deviceId })));
        long revision = _store.Current.Revision;
        long catalogueRevision = _store.Current.CatalogueRevision;
        // Another app saves with this revision while the device page sends commands that change nothing.
        string other = Command("policy", "other", new { source = "casting", policy = "connected" });
        bool changed = false;
        _store.Changed += () => changed = true;
        foreach ((string action, string requestId, object fields) in new (string, string, object)[]
        {
            ("linkCasting", "again", new { profileId = "first", deviceId }),
            ("unlinkCasting", "not linked", new { profileId = "second", deviceId }),
            ("forgetCasting", "unknown", new { deviceId = new string('0', 64) }),
        })
        {
            JsonElement result = await RunCommand(coordinator, action, requestId, fields);
            Assert.True(Succeeded(result));
            Assert.Equal(revision, result.GetProperty("revision").GetInt64());
        }
        Assert.False(changed);
        Assert.Equal((revision, catalogueRevision), (_store.Current.Revision, _store.Current.CatalogueRevision));
        var client = new FakeMqttClientService();
        var handler = new ProfilePlaybackHandler(coordinator, "Command");
        await handler.HandleMessageAsync(Message(handler.Topic, other), client);
        await handler.CommandsCompleted;
        Assert.True(Succeeded(Published(client, "Result/other")));
    }

    [Fact]
    public async Task CastingIsOfferedOnlyWhileTheAudioServiceReportsIt()
    {
        ProfilePlaybackCoordinator coordinator = Coordinator();
        int changes = 0;
        coordinator.Changed += () => changes++;
        bool Available() => JsonSerializer.SerializeToElement(coordinator.CastingDevices()).GetProperty("available").GetBoolean();
        Assert.False(Available());
        var handler = new ProfilePlaybackHandler(coordinator, "CastingStatus");
        Assert.Equal("SyrenSystem/v3/CastingStatus", handler.Topic);
        var client = new FakeMqttClientService();
        await handler.HandleMessageAsync(Message(handler.Topic, "{\"available\":true}"), client);
        Assert.True(Available());
        await handler.HandleMessageAsync(Message(handler.Topic, "{\"available\":true}"), client);
        Assert.Equal(1, changes);
        foreach (string payload in new[] { "{\"available\":false}", "{\"available\":\"yes\"}", "{}", "[]" })
        {
            await handler.HandleMessageAsync(Message(handler.Topic, "{\"available\":true}"), client);
            await handler.HandleMessageAsync(Message(handler.Topic, payload), client);
            Assert.False(Available(), payload);
        }
        Assert.Empty(client.Published);
    }

    [Fact]
    public void AConnectedCastingDeviceIsListedAsConnectedAndFirst()
    {
        _catalogue.BeginGeneration();
        Accepted(CastingEvent("start", 1) with { SessionId = "earlier", AccountId = "airplay:device:01", AccountName = "Earlier" });
        Accepted(CastingEvent("end", 2) with { SessionId = "earlier", AccountId = "airplay:device:01" });
        _catalogue.Expire(expireOwners: false);
        Accepted(CastingEvent("start", 1));
        Accepted(CastingEvent("play", 2));
        Accepted(CastingEvent("pause", 3));
        _clock.Advance(600);
        _catalogue.Expire(expireOwners: false);
        Accepted(CastingEvent("start", 1) with { SessionId = "later", ProducerId = "keuken", AccountId = "airplay:device:02", AccountName = "Later" });
        Accepted(CastingEvent("end", 2) with { SessionId = "later", ProducerId = "keuken", AccountId = "airplay:device:02" });
        _catalogue.Expire(expireOwners: false);
        JsonElement[] Devices() => JsonSerializer.SerializeToElement(Coordinator().CastingDevices()).GetProperty("devices")
            .EnumerateArray().ToArray();
        // The paused phone started 10 minutes ago, yet it is connected now, so it comes before the device seen just now.
        Assert.Equal(["Phone", "Later", "Earlier"], Devices().Select(device => device.GetProperty("name").GetString()!).ToArray());
        Assert.Equal([true, false, false], Devices().Select(device => device.GetProperty("connected").GetBoolean()).ToArray());
        Assert.False(Devices()[0].GetProperty("playing").GetBoolean());
        _clock.Advance(1);
        Accepted(CastingEvent("end", 4));
        _catalogue.Expire(expireOwners: false);
        Assert.Equal(["Phone", "Later", "Earlier"], Devices().Select(device => device.GetProperty("name").GetString()!).ToArray());
        Assert.False(Devices()[0].GetProperty("connected").GetBoolean());
    }

    [Fact]
    public async Task ProfileSaveKeepsLinkedCastingDevices()
    {
        _catalogue.BeginGeneration();
        Accepted(CastingEvent("start", 1));
        string deviceId = Session.SenderId!;
        ProfilePlaybackCoordinator coordinator = Coordinator();
        Assert.True(Succeeded(await RunCommand(coordinator, "linkCasting", "link", new { profileId = "first", deviceId })));
        LinkedCastingDevice[] linked = [new LinkedCastingDevice { Id = deviceId, Name = "Phone" }];
        Assert.True(Succeeded(await RunCommand(coordinator, "profile", "old-app", new
        {
            profile = new { id = "first", name = "Renamed", sourcePriority = new[] { "spotify", "laptop", "casting" } },
        })));
        ListenerProfile saved = _store.Current.Profiles.Single(profile => profile.Id == "first");
        Assert.Equal("Renamed", saved.Name);
        Assert.Equal(linked, saved.CastingDevices);
        foreach (object castingDevices in new object[] { Array.Empty<object>(), new[] { new { id = new string('b', 64), name = "Forged" } } })
        {
            Assert.True(Succeeded(await RunCommand(coordinator, "profile", "new-app", new
            {
                profile = new { id = "first", name = "Renamed", sourcePriority = new[] { "spotify", "laptop", "casting" }, castingDevices },
            })));
            Assert.Equal(linked, _store.Current.Profiles.Single(profile => profile.Id == "first").CastingDevices);
        }
        Assert.True(Succeeded(await RunCommand(coordinator, "profile", "added", new
        {
            profile = new
            {
                id = "third", name = "Third", sourcePriority = new[] { "spotify", "laptop", "casting" },
                castingDevices = new[] { new { id = new string('c', 64), name = "Forged" } },
            },
        })));
        Assert.Empty(_store.Current.Profiles.Single(profile => profile.Id == "third").CastingDevices);
        Assert.Equal("first", Session.OwnerId);
    }

    [Fact]
    public async Task InvalidCastingProtocolAccountOrDisabledDestinationIsRejected()
    {
        _store.Save(_store.Current with
        {
            Groups = [new PersistentPlaybackGroup { Id = "kantoor", Name = "Kantoor", SourcePriority = ["spotify", "laptop"] }],
        });
        _catalogue.BeginGeneration();
        SessionLifecycleEvent[] refused =
        [
            CastingEvent("start", 1) with { Protocol = null },
            CastingEvent("start", 1) with { Protocol = "AirPlay", AccountId = "AirPlay:device:aa" },
            CastingEvent("start", 1) with { Protocol = "1airplay", AccountId = "1airplay:device:aa" },
            CastingEvent("start", 1) with { Protocol = "airplay-protocol-x", AccountId = "airplay-protocol-x:device:aa" },
            CastingEvent("start", 1) with { AccountId = null },
            CastingEvent("start", 1) with { AccountId = "dlna:device:aa" },
            CastingEvent("start", 1) with { AccountId = "airplay:device:a a" },
            CastingEvent("start", 1) with { AccountId = "airplay:device:aa\n" },
            CastingEvent("start", 1) with { AccountId = "airplay:device:é" },
            CastingEvent("start", 1) with { AccountId = "airplay:device:" + new string('a', 242) },
            CastingEvent("start", 1) with { Destination = "kantoor" },
            CastingEvent("start", 1) with { Destination = "missing" },
        ];
        var client = new FakeMqttClientService();
        var handler = new ProfilePlaybackHandler(Coordinator(), "Lifecycle");
        foreach (SessionLifecycleEvent message in refused)
        {
            Assert.Equal(LifecycleOutcome.UnknownSession, _catalogue.Apply(message));
            await handler.HandleMessageAsync(Message(handler.Topic, JsonSerializer.Serialize(message)), client);
        }
        Assert.Equal(refused.Length, client.Published.Count(publish => publish.Topic == ProfilePlaybackHandler.Prefix + "LifecycleRejected"));
        Assert.Empty(_store.Current.Sessions);
        Assert.Null(_store.Current.CastingKey);
        Assert.Equal(LifecycleOutcome.Accepted, _catalogue.Apply(CastingEvent("start", 1) with
        {
            AccountId = "airplay:device:" + new string('a', 241),
        }));
        Assert.Equal(256, Session.AccountId!.Length);
    }

    [Fact]
    public async Task CastingStartWithAnUnusableNameIsAcceptedWithoutARegistryEntry()
    {
        _catalogue.BeginGeneration();
        string?[] names = [null, "", "   ", "\u0001\u0007\u001b", "\t\r\n", "\ud800"];
        for (int index = 0; index < names.Length; index++)
        {
            Assert.True(Accepted(CastingEvent("start", 1) with
            {
                SessionId = "unnamed-" + index, AccountId = "airplay:device:" + index, AccountName = names[index],
            }));
        }
        Assert.Equal(names.Length, _store.Current.Sessions.Count);
        Assert.Empty(_registry.Devices());
        string longName = "\u0007 Café " + string.Concat(Enumerable.Repeat("\U0001F44D\U0001F3FD", 150));
        Assert.True(Accepted(CastingEvent("start", 1) with { SessionId = "named", AccountId = "airplay:dacp:ABC", AccountName = longName }));
        CastingDeviceEntry device = Assert.Single(_registry.Devices());
        Assert.StartsWith("Café \U0001F44D", device.Name);
        Assert.Equal(CastingDeviceRegistry.MaxNameLength, new StringInfo(device.Name).LengthInTextElements);
        Assert.True(device.Name.Length > CastingDeviceRegistry.MaxNameLength);
        Assert.True(Succeeded(await RunCommand(Coordinator(), "linkCasting", "link", new { profileId = "first", deviceId = device.Id })));
        Assert.Equal(device.Name, _store.Current.Profiles.Single(profile => profile.Id == "first").CastingDevices.Single().Name);
    }

    [Fact]
    public void CastingNamesWithManyCombiningMarksAreCappedInCodeUnits()
    {
        // Each element is a letter with four marks, so 100 elements would be 500 code units.
        string marked = string.Concat(Enumerable.Repeat("x\u0301\u0302\u0303\u0304", 150));
        string? cleaned = CastingDeviceRegistry.CleanName(marked);
        Assert.Equal(CastingDeviceRegistry.MaxNameUnits, cleaned!.Length);
        Assert.Equal(80, new StringInfo(cleaned).LengthInTextElements);
        Assert.Equal("Phone", CastingDeviceRegistry.CleanName("Phone a" + new string('\u0301', 200_000)));
        Assert.Null(CastingDeviceRegistry.CleanName("a" + new string('\u0301', 200_000)));
        _catalogue.BeginGeneration();
        Assert.True(Accepted(CastingEvent("start", 1) with { AccountName = string.Concat(Enumerable.Repeat("a" + new string('\u0301', 20_000), 100)) }));
        Assert.Single(_store.Current.Sessions);
        Assert.Empty(_registry.Devices());
    }

    [Fact]
    public void CastingRegistryIsBoundedWindowedAndHasNoAccounts()
    {
        _catalogue.BeginGeneration();
        Accepted(CastingEvent("start", 1) with { SessionId = "open", AccountId = "airplay:device:open", AccountName = "Open" });
        for (int index = 0; index < CastingDeviceRegistry.Capacity; index++)
        {
            _clock.Advance(1);
            string sessionId = "closed-" + index;
            Accepted(CastingEvent("start", 1) with
            {
                SessionId = sessionId, AccountId = $"airplay:device:{index:x2}", AccountName = "Closed " + index,
            });
            Accepted(CastingEvent("end", 2) with { SessionId = sessionId });
            _catalogue.Expire(expireOwners: false);
        }
        string[] Names() => _registry.Devices().Select(device => device.Name).ToArray();
        Assert.Equal(CastingDeviceRegistry.Capacity, Names().Length);
        Assert.Contains("Open", Names());
        Assert.DoesNotContain("Closed 0", Names());
        Assert.Contains("Closed 1", Names());
        Accepted(CastingEvent("start", 1) with { SessionId = "address", AccountId = "airplay:address:192.168.2.50", AccountName = "Somebody" });
        Assert.DoesNotContain("Somebody", Names());
        string payload = JsonSerializer.Serialize(Coordinator().CastingDevices());
        foreach (string hidden in new[] { "accountId", "airplay:", "model", "192.168" })
        {
            Assert.DoesNotContain(hidden, payload);
        }
        JsonElement listed = JsonSerializer.SerializeToElement(Coordinator().CastingDevices()).GetProperty("devices")[0];
        Assert.Equal(["id", "name", "protocol", "lastSeen", "playing", "connected", "profileId", "ownerId"],
            listed.EnumerateObject().Select(property => property.Name).ToArray());
        // Closed 1 to 10 ended 2 to 11 seconds in, so they leave the window before the rest.
        _clock.Advance((int)CastingDeviceRegistry.Window.TotalSeconds - CastingDeviceRegistry.Capacity + 11);
        Assert.Equal(Enumerable.Range(11, 21).Select(index => "Closed " + index).Append("Open").Order(StringComparer.Ordinal),
            Names().Order(StringComparer.Ordinal));
        Assert.Null(_registry.Find(CastingDevice("airplay:device:01")));
        Assert.NotNull(_registry.Find(CastingDevice("airplay:device:0b")));
        Assert.Equal(CastingDeviceRegistry.Capacity, _registry.Count);
        _catalogue.Expire(expireOwners: false);
        Assert.Equal(22, _registry.Count);
        _clock.Advance((int)CastingDeviceRegistry.Window.TotalSeconds);
        _catalogue.Expire(expireOwners: false);
        Assert.Equal(["Open"], Names());
        Assert.Equal(1, _registry.Count);
        Accepted(CastingEvent("end", 2) with { SessionId = "open" });
        _catalogue.Expire(expireOwners: false);
        _clock.Advance((int)CastingDeviceRegistry.Window.TotalSeconds - 1);
        _catalogue.Expire(expireOwners: false);
        Assert.Equal(["Open"], Names());
        _clock.Advance(1);
        _catalogue.Expire(expireOwners: false);
        Assert.Empty(Names());
    }

    [Fact]
    public void GenerationChangeKeepsCastingIneligibleUntilReconcile()
    {
        _catalogue.BeginGeneration();
        Accepted(CastingEvent("start", 1));
        Accepted(CastingEvent("play", 2));
        SessionLifecycleEvent stale = CastingEvent("reconcile", 3);
        _catalogue.BeginGeneration();
        Assert.All(Session.Transports, transport => Assert.False(transport.Available));
        _clock.Advance(3);
        Assert.True(Accepted(CastingEvent("heartbeat", 2)));
        Assert.False(Session.Eligible);
        Assert.False(Accepted(stale));
        Assert.False(Session.Eligible);
        Assert.True(Accepted(CastingEvent("reconcile", 3)));
        Assert.True(Session.Eligible);
        Assert.Equal(1, Session.ClaimSequence);
        Assert.Equal(1, _store.Current.ClaimSequence);
    }

    [Fact]
    public void ValidationRejectsDuplicateLinksAndProtocolOrSenderOnOtherSources()
    {
        string deviceId = new('a', 64);
        ListenerProfile first = _store.Current.Profiles[0];
        ListenerProfile second = _store.Current.Profiles[1];
        var casting = new PlaybackSession
        {
            Id = "cast", ProducerId = "kantoor", OwnerId = "first", Source = "casting", Destination = "house",
            EventSequence = 1, Protocol = "airplay", SenderId = deviceId,
        };
        var spotify = new PlaybackSession
        {
            Id = "spotify", ProducerId = "receiver", OwnerId = "first", Source = "spotify", Destination = "house", EventSequence = 1,
        };
        LinkedCastingDevice Linked(string id, string name = "Phone") => new() { Id = id, Name = name };
        PersistentSystemState valid = _store.Current with
        {
            CastingKey = Convert.ToBase64String(new byte[32]),
            Profiles = [first with { CastingDevices = [Linked(deviceId)] }, second],
            Sessions = [casting, spotify],
        };
        ProfilePlaybackValidation.Validate(valid);
        PersistentSystemState WithDevices(params LinkedCastingDevice[] devices) => valid with
        {
            Profiles = [first with { CastingDevices = devices.ToList() }, second],
        };
        (PersistentSystemState State, string Error)[] invalid =
        [
            (valid with { Profiles = [first with { CastingDevices = [Linked(deviceId)] }, second with { CastingDevices = [Linked(deviceId)] }] },
                "Linked casting devices are invalid"),
            (WithDevices(Linked(deviceId), Linked(deviceId)), "Linked casting devices are invalid"),
            (WithDevices(Linked(new string('A', 64))), "Linked casting devices are invalid"),
            (WithDevices(Linked(new string('a', 63))), "Linked casting devices are invalid"),
            (WithDevices(Enumerable.Range(0, 17).Select(index => Linked(index.ToString("x64"))).ToArray()), "Linked casting devices are invalid"),
            (WithDevices(Linked(deviceId, " ")), "Linked casting devices are invalid"),
            (WithDevices(Linked(deviceId, new string('n', 101))), "Linked casting devices are invalid"),
            (WithDevices(Linked(deviceId, "x" + new string('\u0301', CastingDeviceRegistry.MaxNameUnits))), "Linked casting devices are invalid"),
            (valid with { CastingKey = "not a key" }, "Linked casting devices are invalid"),
            (valid with { CastingKey = Convert.ToBase64String(new byte[31]) }, "Linked casting devices are invalid"),
            (valid with { Sessions = [casting, spotify with { Protocol = "airplay" }] }, "Session ledger is invalid"),
            (valid with { Sessions = [casting, spotify with { SenderId = deviceId }] }, "Session ledger is invalid"),
            (valid with { Sessions = [casting with { Protocol = "AirPlay" }] }, "Session ledger is invalid"),
            (valid with { Sessions = [casting with { Protocol = null }] }, "Session ledger is invalid"),
            (valid with { Sessions = [casting with { SenderId = null }] }, "Session ledger is invalid"),
            (valid with { Sessions = [casting with { SenderId = "guest:" + deviceId }] }, "Session ledger is invalid"),
        ];
        foreach ((PersistentSystemState state, string error) in invalid)
        {
            Assert.Equal(error, Assert.Throws<InvalidDataException>(() => ProfilePlaybackValidation.Validate(state)).Message);
        }
        ProfilePlaybackValidation.Validate(WithDevices(Enumerable.Range(0, 16).Select(index => Linked(index.ToString("x64"))).ToArray()));
        Assert.True(ProfilePlaybackValidation.ValidProtocol("airplay"));
        Assert.True(ProfilePlaybackValidation.ValidProtocol("dlna-bridge-2"));
        foreach (string? protocol in new[] { null, "", "AirPlay", "2airplay", "airplay\n", "air play", "a" + new string('b', 16) })
        {
            Assert.False(ProfilePlaybackValidation.ValidProtocol(protocol));
        }
    }

    [Fact]
    public async Task SpotifyStartStillDoesNotClaimWhenCastingIsConfigured()
    {
        _catalogue.BeginGeneration();
        object group = new
        {
            groupId = "kantoor", name = "Kantoor", speakerIds = Array.Empty<string>(),
            enabledSources = new[] { "spotify", "laptop", "casting" }, sourceLevels = new Dictionary<string, double> { ["casting"] = 80 },
            masterVolume = 100, muted = false,
        };
        Assert.False(Succeeded(await RunCommand(Coordinator(), "group", "without", group)));
        ProfilePlaybackCoordinator coordinator = Coordinator(casting: true);
        Assert.True(Succeeded(await RunCommand(coordinator, "group", "with", group)));
        Assert.Contains(JsonSerializer.SerializeToElement(coordinator.Configuration()).GetProperty("sources").EnumerateArray(),
            source => source.GetProperty("id").GetString() == "casting");
        Assert.True(Accepted(CastingEvent("start", 1) with { SessionId = "cast", Destination = "kantoor" }));
        Assert.True(Accepted(Event("start", 1) with { Destination = "kantoor" }));
        PlaybackSession Spotify() => _store.Current.Sessions.Single(session => session.Id == "session");
        Assert.False(Spotify().Claimed);
        Assert.Equal("connected", Spotify().State);
        Assert.Null(Spotify().Protocol);
        Assert.Null(Spotify().SenderId);
        Assert.Equal(1, _store.Current.ClaimSequence);
        Assert.True(Accepted(Event("play", 2)));
        Assert.True(Spotify().Claimed);
        Assert.Equal(2, Spotify().ClaimSequence);
    }

    [Fact]
    public async Task SessionTransportBindingsBindsACastingStream()
    {
        PrepareSpeaker();
        _store.Save(_store.Current with
        {
            Groups = [_store.Current.Groups.Single() with { SourcePriority = ["spotify", "laptop", "casting"] }],
            Sessions = [],
        });
        _catalogue.BeginGeneration();
        Accepted(CastingEvent("start", 1) with { Destination = "group" });
        Accepted(CastingEvent("play", 2));
        string clientId = SessionTransportBindings.ClientIdentity("client", "airplay-kantoor");
        var snapcast = new RecordingSnapCastService
        {
            Status = new Syren.Server.Models.SnapCast.SnapServerStatus
            {
                Groups =
                [
                    new Syren.Server.Models.SnapCast.SnapGroupStatus
                    {
                        Id = "snap-group", StreamId = "syren-priority-old", Muted = true,
                        Clients =
                        [
                            new Syren.Server.Models.SnapCast.SnapClientStatus
                            {
                                Id = clientId, Connected = true,
                                Config = new Syren.Server.Models.SnapCast.SnapClientConfigStatus
                                {
                                    Volume = new Syren.Server.Models.SnapCast.SnapClientVolumeStatus { Percent = 40 },
                                },
                            },
                        ],
                    },
                ],
                Streams = [new Syren.Server.Models.SnapCast.SnapStreamStatus { Id = "airplay-kantoor", Status = "playing" }],
            },
        };
        ProfilePlaybackCoordinator coordinator = Coordinator(snapcast);
        Assert.True(coordinator.ReceiverStatus(JsonSerializer.SerializeToElement(new
        {
            protocolVersion = 3, generation = _store.Current.Generation, speakerId = "speaker", physicalClientId = "client",
            instanceId = "receiver", bootSequence = 1, sequence = 1, ready = true,
            inputs = new[] { new { sessionId = "session", transportId = "airplay-kantoor", clientId, receiving = true } },
        })));
        var bindings = new SessionTransportBindings(coordinator, _store, snapcast, NullLogger<SessionTransportBindings>.Instance);
        await bindings.ReconcileAsync(CancellationToken.None);
        Assert.Equal([("snap-group", "airplay-kantoor")], snapcast.GroupStreamChanges);
        Assert.Equal([("snap-group", false)], snapcast.GroupMuteChanges);
        Assert.Equal([(clientId, 100)], snapcast.VolumeChanges);
        Assert.Equal("airplay-kantoor", bindings.Confirmed[clientId]);
        _store.Save(_store.Current with { Groups = [_store.Current.Groups.Single() with { SourcePriority = ["spotify", "laptop"] }] });
        await bindings.ReconcileAsync(CancellationToken.None);
        Assert.Empty(bindings.Confirmed);
        Assert.Single(snapcast.GroupStreamChanges);
    }

    [Fact]
    public void ProducerVolumeScalesGainsWithoutChangingRevisions()
    {
        ProfilePlaybackCoordinator coordinator = PrepareSpotifyGains();
        Assert.Equal(0.4, SpotifyGain(coordinator), 10);
        PersistentSystemState written = _store.Current;
        Assert.Equal(LifecycleOutcome.Accepted, coordinator.Lifecycle(Payload(Event("heartbeat", 2), 0.5)));
        Assert.Equal(0.2, SpotifyGain(coordinator), 10);
        Assert.Same(written, _store.Current);
        JsonElement gains = JsonSerializer.SerializeToElement(coordinator.Gains());
        Assert.Equal(written.Revision, gains.GetProperty("configurationRevision").GetInt64());
        Assert.Equal(written.CatalogueRevision, gains.GetProperty("catalogueRevision").GetInt64());
        // A bad volume is skipped, but the heartbeat that carried it still counts.
        foreach (object invalid in new object[] { "loud", 1.5, -0.1, true })
        {
            Assert.Equal(LifecycleOutcome.Accepted, coordinator.Lifecycle(Payload(Event("heartbeat", 2), invalid)));
            Assert.Equal(0.2, SpotifyGain(coordinator), 10);
        }
        Assert.Equal(LifecycleOutcome.Accepted, coordinator.Lifecycle(Payload(Event("pause", 3))));
        Assert.Equal(0.2, SpotifyGain(coordinator), 10);
        // An event the catalogue refuses never sets a volume.
        Assert.Equal(LifecycleOutcome.Ignored, coordinator.Lifecycle(Payload(Event("heartbeat", 3) with { ProducerId = "other" }, 0.1)));
        Assert.Equal(1.0, _volumes.Volume("other"));
        Assert.Equal(LifecycleOutcome.Accepted, coordinator.Lifecycle(Payload(Event("heartbeat", 3), 0)));
        Assert.Equal(ProfilePositionService.SilentGain, SpotifyGain(coordinator));
        Assert.Equal(LifecycleOutcome.Accepted, coordinator.Lifecycle(Payload(Event("heartbeat", 3), 1)));
        Assert.Equal(0.4, SpotifyGain(coordinator), 10);
    }

    [Fact]
    public void ProducerVolumeKeepsGainsWithinRange()
    {
        ProfilePlaybackCoordinator coordinator = PrepareSpotifyGains();
        _store.Save(_store.Current with
        {
            Speakers = [_store.Current.Speakers.Single() with { Volume = 400 }],
        });
        Assert.Equal(1, SpotifyGain(coordinator));
        coordinator.Lifecycle(Payload(Event("heartbeat", 2), 0.1));
        Assert.Equal(0.2, SpotifyGain(coordinator), 10);
    }

    [Fact]
    public async Task ProducerVolumeRepublishesOnlyGainsAndIsSavedOnShutdown()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"syren-volumes-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, ProducerVolumeStore.FileName);
        try
        {
            var volumes = new ProducerVolumeStore(path, _clock);
            ProfilePlaybackCoordinator coordinator = PrepareSpotifyGains(volumes);
            var mqtt = new FakeMqttClientService { ConnectionEpoch = 1 };
            mqtt.SetConnected(true);
            var bindings = new SessionTransportBindings(coordinator, _store, new RecordingSnapCastService(),
                NullLogger<SessionTransportBindings>.Instance);
            using var publisher = new ProfilePlaybackPublisher(coordinator, _catalogue, bindings, _store, volumes, mqtt, _clock,
                NullLogger<ProfilePlaybackPublisher>.Instance);
            await publisher.StartAsync(CancellationToken.None);
            try
            {
                await PublisherPasses(mqtt);
                int Count(string topic) => mqtt.Published.Count(publish => publish.Topic == ProfilePlaybackHandler.Prefix + topic);
                double PublishedGain() => Published(mqtt, "Gains").GetProperty("gains").GetProperty("speaker").GetProperty("session").GetDouble();
                int configurations = Count("Configuration");
                int catalogues = Count("Catalogue");
                Assert.Equal(0.4, PublishedGain(), 10);
                coordinator.Lifecycle(Payload(Event("heartbeat", 2), 0.5));
                await TestServices.WaitUntilAsync(() => Math.Abs(PublishedGain() - 0.2) < 1e-9);
                await TestServices.WaitUntilAsync(() => File.Exists(path));
                Assert.Equal(0.5, SavedVolumes(path)["receiver"]);
                // The second change falls inside the save interval, so only shutdown writes it.
                coordinator.Lifecycle(Payload(Event("heartbeat", 2), 0.25));
                await TestServices.WaitUntilAsync(() => Math.Abs(PublishedGain() - 0.1) < 1e-9);
                await PublisherPasses(mqtt);
                Assert.Equal(0.5, SavedVolumes(path)["receiver"]);
                Assert.Equal(configurations, Count("Configuration"));
                Assert.Equal(catalogues, Count("Catalogue"));
            }
            finally
            {
                await publisher.StopAsync(CancellationToken.None);
            }
            Assert.Equal(0.25, SavedVolumes(path)["receiver"]);
            Assert.Equal(0.25, new ProducerVolumeStore(path, _clock).Volume("receiver"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ProducerVolumesAreSavedAtMostOncePerIntervalAndOnlyWhenChanged()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"syren-volumes-{Guid.NewGuid():N}");
        string path = ProducerVolumeStore.PathNextTo(Path.Combine(directory, "state.json"));
        Assert.Equal(Path.Combine(directory, "producer-volumes.json"), path);
        try
        {
            var volumes = new ProducerVolumeStore(path, _clock);
            Assert.Equal(1.0, volumes.Volume("first"));
            volumes.SaveIfDue();
            Assert.False(File.Exists(path));
            Assert.True(volumes.Set("first", 0.5));
            Assert.False(volumes.Set("first", 0.5));
            Assert.False(volumes.Set("first", double.NaN));
            Assert.False(volumes.Set("first", 2));
            volumes.SaveIfDue();
            Assert.Equal(0.5, SavedVolumes(path)["first"]);
            volumes.Set("first", 0.4);
            volumes.SaveIfDue();
            _clock.Advance((int)ProducerVolumeStore.SaveInterval.TotalSeconds - 1);
            volumes.SaveIfDue();
            Assert.Equal(0.5, SavedVolumes(path)["first"]);
            _clock.Advance(1);
            volumes.SaveIfDue();
            Assert.Equal(0.4, SavedVolumes(path)["first"]);
            // Nothing changed, so nothing is written even after the interval.
            File.Delete(path);
            _clock.Advance(60);
            volumes.SaveIfDue();
            volumes.Flush();
            Assert.False(File.Exists(path));
            volumes.Set("second", 0.3);
            volumes.Flush();
            Assert.Equal(new Dictionary<string, double> { ["first"] = 0.4, ["second"] = 0.3 }, SavedVolumes(path));
            var loaded = new ProducerVolumeStore(path, _clock);
            Assert.Equal(0.4, loaded.Volume("first"));
            Assert.Equal(0.3, loaded.Volume("second"));
            Assert.Equal(1.0, loaded.Volume("unknown"));
            File.WriteAllText(path, """{"first": 0.6, "second": "loud", "third": 7, "fourth": -1}""");
            loaded = new ProducerVolumeStore(path, _clock);
            Assert.Equal(0.6, loaded.Volume("first"));
            Assert.Equal(1.0, loaded.Volume("second"));
            Assert.Equal(1.0, loaded.Volume("third"));
            Assert.Equal(1.0, loaded.Volume("fourth"));
            File.WriteAllText(path, "not json");
            Assert.Equal(1.0, new ProducerVolumeStore(path, _clock).Volume("first"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void PausedSpotifySessionEndsAfterTwelveHours()
    {
        _catalogue.BeginGeneration();
        Accepted(Event("start", 1));
        Accepted(Event("play", 2));
        Accepted(Event("pause", 3));
        _catalogue.Expire(expireOwners: true);
        _clock.Advance((int)SessionCatalogueService.PausedSpotifyLimit.TotalSeconds - 1);
        Assert.True(Accepted(Event("heartbeat", 3)));
        _catalogue.Expire(expireOwners: true);
        Assert.Equal("paused", Session.State);
        long revision = _store.Current.CatalogueRevision;
        _clock.Advance(1);
        _catalogue.Expire(expireOwners: true);
        Assert.Equal("ended", Session.State);
        Assert.Equal("ended", _catalogue.Snapshot().Sessions.Single().State);
        Assert.Equal(revision + 1, _store.Current.CatalogueRevision);
        Assert.False(Accepted(Event("heartbeat", 3)));
        Assert.False(Accepted(Event("play", 4)));
    }

    [Fact]
    public void PlayingSpotifySessionsNeverExpireAndPlayingRestartsThePausedPeriod()
    {
        _catalogue.BeginGeneration();
        Accepted(Event("start", 1));
        Accepted(Event("play", 2));
        for (int hour = 0; hour < 48; hour++)
        {
            _clock.Advance(3600);
            Accepted(Event("heartbeat", 2));
            _catalogue.Expire(expireOwners: true);
        }
        Assert.Equal("playing", Session.State);
        Accepted(Event("pause", 3));
        _catalogue.Expire(expireOwners: true);
        _clock.Advance(11 * 3600);
        _catalogue.Expire(expireOwners: true);
        // Play and pause between two expiry passes still restart the paused period.
        Accepted(Event("play", 4));
        Accepted(Event("pause", 5));
        _catalogue.Expire(expireOwners: true);
        _clock.Advance(11 * 3600);
        _catalogue.Expire(expireOwners: true);
        Assert.Equal("paused", Session.State);
        _clock.Advance(3600);
        _catalogue.Expire(expireOwners: true);
        Assert.Equal("ended", Session.State);
    }

    [Fact]
    public void PausedSpotifySessionLoadedAtStartupStillExpires()
    {
        _catalogue.BeginGeneration();
        Accepted(Event("start", 1));
        Accepted(Event("play", 2));
        Accepted(Event("pause", 3));
        var restarted = new SessionCatalogueService(_store, _clock, _registry);
        restarted.BeginGeneration();
        restarted.Expire(expireOwners: true);
        _clock.Advance((int)SessionCatalogueService.PausedSpotifyLimit.TotalSeconds - 1);
        restarted.Expire(expireOwners: true);
        Assert.Equal("paused", Session.State);
        _clock.Advance(1);
        restarted.Expire(expireOwners: true);
        Assert.Equal("ended", Session.State);
    }

    [Fact]
    public void PausedCastingSessionsAreNotEndedByTheSpotifyLimit()
    {
        _catalogue.BeginGeneration();
        Accepted(CastingEvent("start", 1));
        Accepted(CastingEvent("play", 2));
        Accepted(CastingEvent("pause", 3));
        _catalogue.Expire(expireOwners: false);
        _clock.Advance((int)SessionCatalogueService.PausedSpotifyLimit.TotalSeconds + 3600);
        _catalogue.Expire(expireOwners: false);
        Assert.Equal("paused", Session.State);
    }

    [Fact]
    public void ProducerVolumeZeroKeepsTheSessionSelectedButSilent()
    {
        ProfilePlaybackCoordinator coordinator = PrepareSpotifyGains();
        coordinator.Lifecycle(Payload(Event("heartbeat", 2), 0));
        // Receivers drop a session whose gain is 0, so a muted Spotify app would let another claimant play.
        Assert.InRange(SpotifyGain(coordinator), double.Epsilon, ProfilePositionService.SilentGain);
        _store.Save(_store.Current with { Groups = [_store.Current.Groups.Single() with { Muted = true }] });
        Assert.Equal(0, SpotifyGain(coordinator));
        _store.Save(_store.Current with { Groups = [_store.Current.Groups.Single() with { Muted = false, MasterVolume = 0 }] });
        Assert.Equal(0, SpotifyGain(coordinator));
    }

    [Fact]
    public void FailedProducerVolumeSaveIsRetriedAfterTheInterval()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"syren-volumes-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, ProducerVolumeStore.FileName);
        try
        {
            // A directory in place of the file makes the write fail.
            Directory.CreateDirectory(path);
            var volumes = new ProducerVolumeStore(path, _clock);
            volumes.Set("first", 0.5);
            volumes.SaveIfDue();
            Assert.True(Directory.Exists(path));
            Assert.Empty(Directory.GetFiles(directory));
            Directory.Delete(path);
            volumes.SaveIfDue();
            Assert.False(File.Exists(path));
            Assert.Equal(0.5, volumes.Volume("first"));
            _clock.Advance((int)ProducerVolumeStore.SaveInterval.TotalSeconds);
            volumes.SaveIfDue();
            Assert.Equal(0.5, SavedVolumes(path)["first"]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void PausedSpotifyPeriodSurvivesAServerRestart()
    {
        _catalogue.BeginGeneration();
        Accepted(Event("start", 1));
        Accepted(Event("play", 2));
        Accepted(Event("pause", 3));
        _catalogue.Expire(expireOwners: true);
        _clock.Advance(11 * 3600);
        _catalogue.Expire(expireOwners: true);
        Assert.Equal(_clock.GetUtcNow().AddHours(-11), Session.PausedAt);
        Assert.Contains("\"pausedAt\"", JsonSerializer.Serialize(Session));
        Assert.DoesNotContain("pausedAt", JsonSerializer.Serialize(_catalogue.Snapshot()));
        var restarted = new SessionCatalogueService(_store, _clock, _registry);
        restarted.BeginGeneration();
        restarted.Expire(expireOwners: true);
        Assert.Equal("paused", Session.State);
        _clock.Advance(3600 - 1);
        restarted.Expire(expireOwners: true);
        Assert.Equal("paused", Session.State);
        _clock.Advance(1);
        restarted.Expire(expireOwners: true);
        Assert.Equal("ended", Session.State);
        Assert.Null(Session.PausedAt);
    }

    [Fact]
    public void PausedSpotifyPeriodIgnoresWallClockStepsWhileRunning()
    {
        _catalogue.BeginGeneration();
        Accepted(Event("start", 1));
        Accepted(Event("play", 2));
        Accepted(Event("pause", 3));
        _catalogue.Expire(expireOwners: true);
        _clock.StepWallClock(24 * 3600);
        _catalogue.Expire(expireOwners: true);
        Assert.Equal("paused", Session.State);
        Accepted(Event("play", 4));
        Assert.Null(Session.PausedAt);
        _clock.Advance(3600);
        Accepted(Event("pause", 5));
        Assert.Equal(_clock.GetUtcNow(), Session.PausedAt);
        // A repeated pause keeps the original start.
        _clock.Advance(60);
        Accepted(Event("pause", 6));
        Assert.Equal(_clock.GetUtcNow().AddSeconds(-60), Session.PausedAt);
    }

    // A Spotify guest session on a speaker at 80 percent in a group at 50 percent, so its gain starts at 0.4.
    private ProfilePlaybackCoordinator PrepareSpotifyGains(ProducerVolumeStore? volumes = null)
    {
        _store.Save(_store.Current with
        {
            Speakers = [new PersistentSpeakerState { SpeakerId = "speaker", Name = "Speaker", SensorId = "sensor", SnapClientId = "client", Volume = 80, Connected = false }],
            Groups = [new PersistentPlaybackGroup { Id = "group", Name = "Group", SpeakerIds = ["speaker"], SourcePriority = ["spotify", "laptop"], MasterVolume = 50 }],
        });
        _catalogue.BeginGeneration();
        ProfilePlaybackCoordinator coordinator = Coordinator(volumes: volumes);
        Assert.Equal(LifecycleOutcome.Accepted, coordinator.Lifecycle(Payload(Event("start", 1))));
        Assert.Equal(LifecycleOutcome.Accepted, coordinator.Lifecycle(Payload(Event("play", 2))));
        return coordinator;
    }

    private static JsonElement Payload(SessionLifecycleEvent message, object? volume = null)
    {
        JsonObject payload = JsonSerializer.SerializeToNode(message)!.AsObject();
        if (volume != null)
        {
            payload["volume"] = JsonSerializer.SerializeToNode(volume);
        }
        return JsonSerializer.SerializeToElement(payload);
    }

    private static double SpotifyGain(ProfilePlaybackCoordinator coordinator) => JsonSerializer.SerializeToElement(coordinator.Gains())
        .GetProperty("gains").GetProperty("speaker").GetProperty("session").GetDouble();

    private static Dictionary<string, double> SavedVolumes(string path) =>
        JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(path))!;

    private ProfilePlaybackCoordinator Coordinator(RecordingSnapCastService? snapcast = null, bool casting = false,
        ProducerVolumeStore? volumes = null)
    {
        snapcast ??= new RecordingSnapCastService();
        volumes ??= _volumes;
        return new ProfilePlaybackCoordinator(_store,
            TestServices.CreateConfigurationService(_store, new RecordingDistanceService(_store), snapcast, casting: casting),
            _catalogue, new ProfileConfigurationService(_store, _registry), new ProfilePositionService(_store, _clock, volumes),
            new PcSessionService(_store, _catalogue, snapcast, NullLogger<PcSessionService>.Instance), _registry, volumes, _clock);
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

    private SessionLifecycleEvent CastingEvent(string action, long sequence) => Event(action, sequence) with
    {
        ProducerId = "kantoor", Source = "casting", Protocol = "airplay", AccountId = "airplay:device:aa", AccountName = "Phone",
        Transports = [new SessionTransport { Id = "airplay-kantoor", Kind = "snapcast", Endpoint = "airplay-kantoor", Available = true }],
    };

    private string CastingDevice(string account) =>
        SessionCatalogueService.CastingDeviceId(Convert.FromBase64String(_store.Current.CastingKey!), account);

    private async Task<JsonElement> RunCommand(ProfilePlaybackCoordinator coordinator, string action, string requestId, object fields)
    {
        var client = new FakeMqttClientService();
        var handler = new ProfilePlaybackHandler(coordinator, "Command");
        await handler.HandleMessageAsync(Message(handler.Topic, Command(action, requestId, fields)), client);
        await handler.CommandsCompleted;
        return Published(client, "Result/" + requestId);
    }

    private static bool Succeeded(JsonElement result) => result.GetProperty("success").GetBoolean();

    // Waits until the publisher has finished at least one whole pass that started after the call.
    private static Task PublisherPasses(FakeMqttClientService mqtt)
    {
        int start = mqtt.ConnectionChecks;
        return TestServices.WaitUntilAsync(() => mqtt.ConnectionChecks >= start + 2);
    }

    // Like the real store, a save holds the lock and Current changes only once it is written; a test can hold one save open.
    private sealed class PausingSaveStore(PersistentSystemState state) : ISystemStateStore
    {
        private readonly object _sync = new();
        public ManualResetEventSlim? HoldSave;
        public Action? AfterUpdate;
        public readonly ManualResetEventSlim Saving = new();

        public PersistentSystemState Current { get; private set; } = state;

        public event Action? Changed;

        public void Save(PersistentSystemState state) => Update(_ => state);

        public PersistentSystemState Update(Func<PersistentSystemState, PersistentSystemState> update)
        {
            PersistentSystemState next;
            bool saved = false;
            lock (_sync)
            {
                next = update(Current);
                if (!ReferenceEquals(next, Current))
                {
                    if (Interlocked.Exchange(ref HoldSave, null) is { } hold)
                    {
                        Saving.Set();
                        hold.Wait(TimeSpan.FromSeconds(5));
                    }
                    Current = next;
                    saved = true;
                }
            }
            if (saved)
            {
                Changed?.Invoke();
            }
            Interlocked.Exchange(ref AfterUpdate, null)?.Invoke();
            return next;
        }
    }

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
