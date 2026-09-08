using Syren.Server.Models;
using Syren.Server.Models.SnapCast;
using Syren.Server.Services;
using Xunit;

namespace Syren.Server.Tests;

public sealed class SystemConfigurationServiceTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public async Task SourceBalanceRejectsInvalidLevels(double level)
    {
        var stateStore = new MemoryStateStore(CreateStateWithSpeaker());
        var service = TestServices.CreateConfigurationService(stateStore,
            new RecordingDistanceService(stateStore), new RecordingSnapCastService());
        var command = GroupCommand("manual", "Group", "speaker-one");
        var result = await service.UpsertGroupAsync(new UpsertGroupCommand
        {
            RequestId = command.RequestId, ExpectedRevision = command.ExpectedRevision,
            GroupId = command.GroupId, Name = command.Name, SpeakerIds = command.SpeakerIds,
            SourcePriority = command.SourcePriority, VolumeMode = command.VolumeMode,
            MasterVolume = command.MasterVolume, Muted = command.Muted,
            SourceLevels = new() { ["spotify"] = level },
        });
        Assert.False(result.Success);
    }

    [Fact]
    public async Task ConfigureSpeakerSupportsManualSpeakerWithoutSensor()
    {
        var stateStore = new MemoryStateStore(new PersistentSystemState
        {
            StateId = "state",
            Revision = 4,
        });
        var distanceService = new RecordingDistanceService(stateStore);
        SystemConfigurationService service = TestServices.CreateConfigurationService(
            stateStore,
            distanceService,
            new RecordingSnapCastService()
        );

        CommandResultMessage result = await service.ConfigureSpeakerAsync(
            new ConfigureSpeakerCommand
            {
                RequestId = "request",
                ExpectedRevision = 4,
                Name = "Desk speakers",
                SnapClientId = "SYREN-PLAYER-1",
                SensorId = null,
                FullVolumeDistance = 1000,
                MuteDistance = 5000,
            }
        );

        Assert.True(result.Success);
        Assert.Equal(5, result.Revision);
        PersistentSpeakerState speaker = Assert.Single(stateStore.Current.Speakers);
        Assert.Equal("Desk speakers", speaker.Name);
        Assert.Equal("syren-player-1", speaker.SnapClientId);
        Assert.Null(speaker.SensorId);
        Assert.Equal(1, distanceService.ApplyChangeCount);
    }

    [Fact]
    public async Task UpsertGroupCreatesPriorityStreamAndAssignsSnapclients()
    {
        var stateStore = new MemoryStateStore(CreateStateWithSpeaker());
        var distanceService = new RecordingDistanceService(stateStore);
        var snapCastService = new RecordingSnapCastService
        {
            Status = StatusWithGroup(
                "snap-group",
                Client("snap-one", "Kitchen Pi"),
                Client("snap-two", "Other Pi")
            ),
        };
        SystemConfigurationService service = TestServices.CreateConfigurationService(
            stateStore,
            distanceService,
            snapCastService
        );

        CommandResultMessage result = await service.UpsertGroupAsync(new UpsertGroupCommand
        {
            RequestId = "request",
            ExpectedRevision = 2,
            GroupId = "kitchen",
            Name = "Kitchen",
            SpeakerIds = ["speaker-one"],
            SourcePriority = ["laptop", "spotify"],
            VolumeMode = "manual",
            MasterVolume = 70,
            Muted = false,
        });
        await service.ReconcileAsync();

        Assert.True(result.Success);
        Assert.Contains("meta:///laptop/spotify", Assert.Single(snapCastService.AddedStreams));
        Assert.Equal("snap-one", Assert.Single(snapCastService.GroupClientChanges).ClientIds[0]);
        Assert.StartsWith("syren-priority-", Assert.Single(snapCastService.GroupStreamChanges).StreamId);
        Assert.Equal(2, distanceService.ApplyVolumeCount);
    }

    [Fact]
    public async Task SavedConfigurationSucceedsWhenSnapserverIsUnavailable()
    {
        var stateStore = new MemoryStateStore(CreateStateWithSpeaker());
        var snapCastService = new RecordingSnapCastService
        {
            StatusException = new SnapServerUnavailableException("Snapserver unavailable"),
        };
        SystemConfigurationService service = TestServices.CreateConfigurationService(
            stateStore,
            new RecordingDistanceService(stateStore),
            snapCastService
        );

        CommandResultMessage result = await service.SetSpeakerLevelAsync(
            new SetSpeakerLevelCommand
            {
                RequestId = "request",
                ExpectedRevision = 2,
                SpeakerId = "speaker-one",
                Level = 42,
            }
        );

        Assert.True(result.Success);
        Assert.Equal(42, Assert.Single(stateStore.Current.Speakers).Volume);
        Assert.Equal(3, stateStore.Current.Revision);
    }

    [Fact]
    public async Task StaleRevisionDoesNotChangeState()
    {
        var stateStore = new MemoryStateStore(CreateStateWithSpeaker());
        SystemConfigurationService service = TestServices.CreateConfigurationService(
            stateStore,
            new RecordingDistanceService(stateStore),
            new RecordingSnapCastService()
        );

        CommandResultMessage result = await service.SetSpeakerLevelAsync(
            new SetSpeakerLevelCommand
            {
                RequestId = "request",
                ExpectedRevision = 1,
                SpeakerId = "speaker-one",
                Level = 42,
            }
        );

        Assert.False(result.Success);
        Assert.Equal(100, Assert.Single(stateStore.Current.Speakers).Volume);
        Assert.Equal(2, stateStore.Current.Revision);
    }

    [Fact]
    public async Task UnexpectedErrorProducesFailureResult()
    {
        var stateStore = new MemoryStateStore(CreateStateWithSpeaker()) { FailSaves = true };
        SystemConfigurationService service = TestServices.CreateConfigurationService(
            stateStore,
            new RecordingDistanceService(stateStore),
            new RecordingSnapCastService()
        );

        CommandResultMessage result = await service.SetSpeakerLevelAsync(
            new SetSpeakerLevelCommand
            {
                RequestId = "request",
                ExpectedRevision = 2,
                SpeakerId = "speaker-one",
                Level = 42,
            }
        );

        Assert.False(result.Success);
        Assert.Equal("Internal error", result.Error);
        Assert.Equal("request", result.RequestId);
        Assert.Equal(100, Assert.Single(stateStore.Current.Speakers).Volume);
    }

    [Fact]
    public async Task DeleteSpeakerMutesItsFormerSnapclient()
    {
        var stateStore = new MemoryStateStore(CreateStateWithSpeaker());
        var snapCastService = new RecordingSnapCastService
        {
            Status = StatusWithGroup("snap-group", Client("snap-one")),
        };
        SystemConfigurationService service = TestServices.CreateConfigurationService(
            stateStore,
            new RecordingDistanceService(stateStore),
            snapCastService
        );

        CommandResultMessage result = await service.DeleteSpeakerAsync(
            new DeleteSpeakerCommand
            {
                RequestId = "request",
                ExpectedRevision = 2,
                SpeakerId = "speaker-one",
            }
        );
        await service.ReconcileAsync();

        Assert.True(result.Success);
        Assert.Equal(("snap-one", 0), Assert.Single(snapCastService.VolumeChanges));
    }

    [Fact]
    public async Task UpsertGroupKeepsUncalibratedMemberWhenOnlyRenaming()
    {
        var stateStore = new MemoryStateStore(CreateStateWithGroup("automatic", "speaker-one"));
        SystemConfigurationService service = TestServices.CreateConfigurationService(
            stateStore,
            new RecordingDistanceService(stateStore),
            new RecordingSnapCastService()
        );

        CommandResultMessage result = await service.UpsertGroupAsync(
            GroupCommand("automatic", "Renamed", "speaker-one")
        );

        Assert.True(result.Success);
        Assert.Equal("Renamed", Assert.Single(stateStore.Current.Groups).Name);
    }

    [Fact]
    public async Task UpsertGroupRejectsAddingUncalibratedSpeakerToAutomaticGroup()
    {
        var stateStore = new MemoryStateStore(CreateStateWithGroup("automatic", "speaker-one"));
        SystemConfigurationService service = TestServices.CreateConfigurationService(
            stateStore,
            new RecordingDistanceService(stateStore),
            new RecordingSnapCastService()
        );

        CommandResultMessage result = await service.UpsertGroupAsync(
            GroupCommand("automatic", "Group", "speaker-one", "speaker-two")
        );

        Assert.False(result.Success);
        Assert.Contains("calibrated", result.Error);
        Assert.Equal(["speaker-one"], Assert.Single(stateStore.Current.Groups).SpeakerIds);
    }

    [Fact]
    public async Task UpsertGroupRejectsSwitchToAutomaticWithUncalibratedMember()
    {
        var stateStore = new MemoryStateStore(CreateStateWithGroup("manual", "speaker-one"));
        SystemConfigurationService service = TestServices.CreateConfigurationService(
            stateStore,
            new RecordingDistanceService(stateStore),
            new RecordingSnapCastService()
        );

        CommandResultMessage result = await service.UpsertGroupAsync(
            GroupCommand("automatic", "Group", "speaker-one")
        );

        Assert.False(result.Success);
        Assert.Contains("calibrated", result.Error);
        Assert.Equal("manual", Assert.Single(stateStore.Current.Groups).VolumeMode);
    }

    [Fact]
    public async Task ReconcileAnchorsOnAnyOnlineMember()
    {
        var stateStore = new MemoryStateStore(CreateStateWithGroup("manual", "speaker-one", "speaker-two"));
        var snapCastService = new RecordingSnapCastService
        {
            Status = StatusWithGroup("snap-group-b", Client("snap-two"), Client("other")),
        };
        SystemConfigurationService service = TestServices.CreateConfigurationService(
            stateStore,
            new RecordingDistanceService(stateStore),
            snapCastService
        );

        await service.ReconcileAsync();

        (string groupId, string[] clientIds) = Assert.Single(snapCastService.GroupClientChanges);
        Assert.Equal("snap-group-b", groupId);
        Assert.Equal(["snap-one", "snap-two"], clientIds);
    }

    [Fact]
    public async Task ReconcilePassesReportedClientVolumesToDistanceService()
    {
        var stateStore = new MemoryStateStore(CreateStateWithGroup("manual", "speaker-one"));
        var snapCastService = new RecordingSnapCastService
        {
            Status = StatusWithGroup("snap-group", Client("snap-one", volumePercent: 35), Client("other")),
        };
        var distanceService = new RecordingDistanceService(stateStore);
        SystemConfigurationService service = TestServices.CreateConfigurationService(
            stateStore,
            distanceService,
            snapCastService
        );

        await service.ReconcileAsync();

        IReadOnlyDictionary<string, SnapClientVolumeStatus?> reported = Assert.IsAssignableFrom<
            IReadOnlyDictionary<string, SnapClientVolumeStatus?>>(distanceService.LastReportedVolumes);
        Assert.Equal(35, reported["snap-one"]?.Percent);
        Assert.True(reported.ContainsKey("other"));
        Assert.Null(reported["other"]);
    }

    [Fact]
    public async Task ReconcileSkipsGroupWithoutOnlineMembers()
    {
        var stateStore = new MemoryStateStore(CreateStateWithGroup("manual", "speaker-one"));
        var snapCastService = new RecordingSnapCastService
        {
            Status = StatusWithGroup("snap-group", Client("snap-one", connected: false)),
        };
        SystemConfigurationService service = TestServices.CreateConfigurationService(
            stateStore,
            new RecordingDistanceService(stateStore),
            snapCastService
        );

        await service.ReconcileAsync();

        Assert.Empty(snapCastService.GroupClientChanges);
        Assert.Empty(snapCastService.GroupStreamChanges);
        Assert.Empty(snapCastService.GroupNameChanges);
        Assert.Empty(snapCastService.GroupMuteChanges);
    }

    [Fact]
    public async Task ReconcileFetchesStatusOnceAndSkipsUnchangedGroupSettings()
    {
        var stateStore = new MemoryStateStore(CreateStateWithGroup("manual", "speaker-one"));
        var snapCastService = new RecordingSnapCastService
        {
            Status = StatusWithGroup("snap-group", Client("snap-one")),
        };
        SystemConfigurationService service = TestServices.CreateConfigurationService(
            stateStore,
            new RecordingDistanceService(stateStore),
            snapCastService
        );
        await service.ReconcileAsync();
        string streamId = Assert.Single(snapCastService.GroupStreamChanges).StreamId;
        snapCastService.Status = new SnapServerStatus
        {
            Groups =
            [
                new SnapGroupStatus
                {
                    Id = "snap-group",
                    Name = "Group",
                    StreamId = streamId,
                    Muted = false,
                    Clients = [Client("snap-one")],
                },
            ],
            Streams = [new SnapStreamStatus { Id = streamId }],
        };
        snapCastService.GroupClientChanges.Clear();
        snapCastService.GroupNameChanges.Clear();
        snapCastService.GroupStreamChanges.Clear();
        snapCastService.GroupMuteChanges.Clear();
        int statusCalls = snapCastService.StatusCalls;

        await service.ReconcileAsync();

        Assert.Equal(statusCalls + 1, snapCastService.StatusCalls);
        Assert.Empty(snapCastService.GroupClientChanges);
        Assert.Empty(snapCastService.GroupNameChanges);
        Assert.Empty(snapCastService.GroupStreamChanges);
        Assert.Empty(snapCastService.GroupMuteChanges);
        Assert.Empty(snapCastService.AddedStreams.Skip(1));
    }

    [Fact]
    public async Task ReconcileContinuesAfterGroupFailure()
    {
        PersistentSystemState state = CreateStateWithSpeakers("speaker-one", "speaker-two") with
        {
            Groups =
            [
                PersistentStateFactory.CreateDefaultGroup(["speaker-one"], TestServices.SourceIds, "manual")
                    with { Id = "a", Name = "A" },
                PersistentStateFactory.CreateDefaultGroup(["speaker-two"], TestServices.SourceIds, "manual")
                    with { Id = "b", Name = "B" },
            ],
        };
        var stateStore = new MemoryStateStore(state);
        var snapCastService = new RecordingSnapCastService
        {
            Status = new SnapServerStatus
            {
                Groups =
                [
                    new SnapGroupStatus { Id = "snap-group-a", Clients = [Client("snap-one")] },
                    new SnapGroupStatus { Id = "snap-group-b", Clients = [Client("snap-two")] },
                ],
            },
        };
        snapCastService.FailingGroupIds.Add("snap-group-a");
        SystemConfigurationService service = TestServices.CreateConfigurationService(
            stateStore,
            new RecordingDistanceService(stateStore),
            snapCastService
        );

        await service.ReconcileAsync();

        Assert.Equal("snap-group-b", Assert.Single(snapCastService.GroupStreamChanges).GroupId);
    }

    [Fact]
    public async Task ReconcileContinuesAfterStreamCreationFailure()
    {
        PersistentSystemState state = CreateStateWithSpeakers("speaker-one", "speaker-two") with
        {
            Groups =
            [
                PersistentStateFactory.CreateDefaultGroup(["speaker-one"], ["spotify", "laptop"], "manual")
                    with { Id = "a", Name = "A" },
                PersistentStateFactory.CreateDefaultGroup(["speaker-two"], ["laptop", "spotify"], "manual")
                    with { Id = "b", Name = "B" },
            ],
        };
        var stateStore = new MemoryStateStore(state);
        var snapCastService = new RecordingSnapCastService
        {
            Status = new SnapServerStatus
            {
                Groups =
                [
                    new SnapGroupStatus { Id = "snap-group-a", Clients = [Client("snap-one")] },
                    new SnapGroupStatus { Id = "snap-group-b", Clients = [Client("snap-two")] },
                ],
            },
        };
        SystemConfigurationService service = TestServices.CreateConfigurationService(
            stateStore,
            new RecordingDistanceService(stateStore),
            snapCastService
        );
        await service.ReconcileAsync();
        string[] streamIds = snapCastService.AddedStreams.Select(RecordingSnapCastService.StreamName).ToArray();
        Assert.Equal(2, streamIds.Length);
        snapCastService.FailingStreamNames.Add(streamIds[0]);
        snapCastService.AddedStreams.Clear();
        snapCastService.GroupStreamChanges.Clear();

        await service.ReconcileAsync();

        Assert.Equal(streamIds[1], RecordingSnapCastService.StreamName(Assert.Single(snapCastService.AddedStreams)));
        Assert.Equal(2, snapCastService.GroupStreamChanges.Count);
    }

    [Fact]
    public async Task ReconcileMutesOnlyUnmutedUnconfiguredClients()
    {
        var stateStore = new MemoryStateStore(CreateStateWithSpeaker());
        var snapCastService = new RecordingSnapCastService
        {
            Status = StatusWithGroup(
                "snap-group",
                Client("loud", volumePercent: 40),
                Client("quiet", volumePercent: 0)
            ),
        };
        SystemConfigurationService service = TestServices.CreateConfigurationService(
            stateStore,
            new RecordingDistanceService(stateStore),
            snapCastService
        );

        await service.ReconcileAsync();

        Assert.Equal(("loud", 0), Assert.Single(snapCastService.VolumeChanges));
    }

    [Fact]
    public async Task SetSpeakerLevelAppliesVolumesWithoutFullReconcile()
    {
        var stateStore = new MemoryStateStore(CreateStateWithSpeaker());
        var distanceService = new RecordingDistanceService(stateStore);
        var snapCastService = new RecordingSnapCastService();
        SystemConfigurationService service = TestServices.CreateConfigurationService(
            stateStore,
            distanceService,
            snapCastService
        );

        CommandResultMessage result = await service.SetSpeakerLevelAsync(new SetSpeakerLevelCommand
        {
            RequestId = "request",
            ExpectedRevision = 2,
            SpeakerId = "speaker-one",
            Level = 30,
        });

        Assert.True(result.Success);
        Assert.Equal(1, distanceService.ApplyVolumeCount);
        Assert.Equal(0, snapCastService.StatusCalls);
    }

    [Fact]
    public async Task BackgroundReconcileRunsAfterMutation()
    {
        var stateStore = new MemoryStateStore(CreateStateWithSpeaker());
        var snapCastService = new RecordingSnapCastService();
        SystemConfigurationService service = TestServices.CreateConfigurationService(
            stateStore,
            new RecordingDistanceService(stateStore),
            snapCastService,
            reconcileSeconds: 300
        );
        await service.StartAsync(CancellationToken.None);
        await TestServices.WaitUntilAsync(() => snapCastService.StatusCalls >= 1);
        await Task.Delay(50);
        Assert.Equal(1, snapCastService.StatusCalls);

        CommandResultMessage result = await service.UpsertGroupAsync(GroupCommand("manual", "Group", "speaker-one"));
        await TestServices.WaitUntilAsync(() => snapCastService.StatusCalls >= 2);
        await service.StopAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(2, snapCastService.StatusCalls);
    }

    [Fact]
    public async Task ExecuteAsyncSurvivesTaskCanceledFromSnapserver()
    {
        var stateStore = new MemoryStateStore(CreateStateWithSpeaker());
        var snapCastService = new RecordingSnapCastService
        {
            StatusException = new TaskCanceledException("Snapserver timed out"),
        };
        SystemConfigurationService service = TestServices.CreateConfigurationService(
            stateStore,
            new RecordingDistanceService(stateStore),
            snapCastService,
            reconcileSeconds: 300
        );
        await service.StartAsync(CancellationToken.None);
        await TestServices.WaitUntilAsync(() => snapCastService.StatusCalls >= 1);
        snapCastService.StatusException = null;

        await service.UpsertGroupAsync(GroupCommand("manual", "Group", "speaker-one"));
        await TestServices.WaitUntilAsync(() => snapCastService.StatusCalls >= 2);
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(2, snapCastService.StatusCalls);
        Assert.True(service.CurrentRuntime.SnapserverOnline);
    }

    [Fact]
    public async Task RuntimeChangedRaisedOnlyWhenSnapshotChanges()
    {
        var stateStore = new MemoryStateStore(CreateStateWithSpeaker());
        var snapCastService = new RecordingSnapCastService
        {
            Status = StatusWithGroup("snap-group", Client("snap-one", "Kitchen Pi")),
        };
        SystemConfigurationService service = TestServices.CreateConfigurationService(
            stateStore,
            new RecordingDistanceService(stateStore),
            snapCastService
        );
        int changes = 0;
        service.RuntimeChanged += () => changes++;

        await service.ReconcileAsync();
        await service.ReconcileAsync();
        Assert.Equal(1, changes);
        Assert.True(service.CurrentRuntime.SnapserverOnline);
        Assert.Equal("Kitchen Pi", Assert.Single(service.CurrentRuntime.OnlineSnapClients).Name);

        snapCastService.StatusException = new SnapServerUnavailableException("down");
        await Assert.ThrowsAsync<SnapServerUnavailableException>(() => service.ReconcileAsync());
        await Assert.ThrowsAsync<SnapServerUnavailableException>(() => service.ReconcileAsync());

        Assert.Equal(2, changes);
        Assert.False(service.CurrentRuntime.SnapserverOnline);
        Assert.Empty(service.CurrentRuntime.OnlineSnapClients);
    }

    private static UpsertGroupCommand GroupCommand(
        string volumeMode,
        string name,
        params string[] speakerIds) => new()
    {
        RequestId = "request",
        ExpectedRevision = 2,
        GroupId = "group",
        Name = name,
        SpeakerIds = speakerIds,
        SourcePriority = TestServices.SourceIds,
        VolumeMode = volumeMode,
        MasterVolume = 100,
        Muted = false,
    };

    private static SnapServerStatus StatusWithGroup(string groupId, params SnapClientStatus[] clients) => new()
    {
        Groups = [new SnapGroupStatus { Id = groupId, Clients = clients }],
        Streams =
        [
            new SnapStreamStatus { Id = "spotify", Status = "idle" },
            new SnapStreamStatus { Id = "laptop", Status = "idle" },
        ],
    };

    private static SnapClientStatus Client(
        string id,
        string? hostName = null,
        bool connected = true,
        int? volumePercent = null) => new()
    {
        Id = id,
        Connected = connected,
        Host = hostName == null ? null : new SnapHostStatus { Name = hostName },
        Config = volumePercent == null
            ? null
            : new SnapClientConfigStatus
            {
                Volume = new SnapClientVolumeStatus { Percent = volumePercent.Value },
            },
    };

    private static PersistentSystemState CreateStateWithSpeaker() => CreateStateWithSpeakers("speaker-one");

    private static PersistentSystemState CreateStateWithSpeakers(params string[] speakerIds) => new()
    {
        StateId = "state",
        Revision = 2,
        Speakers = speakerIds.Select(speakerId => new PersistentSpeakerState
        {
            SpeakerId = speakerId,
            Name = $"Speaker {speakerId}",
            SnapClientId = speakerId.Replace("speaker-", "snap-"),
            FullVolumeDistance = 1000,
            MuteDistance = 5000,
            Connected = false,
            Volume = 100,
        }).ToList(),
    };

    private static PersistentSystemState CreateStateWithGroup(string volumeMode, params string[] speakerIds) =>
        CreateStateWithSpeakers("speaker-one", "speaker-two") with
        {
            Groups =
            [
                PersistentStateFactory.CreateDefaultGroup(speakerIds, TestServices.SourceIds, volumeMode)
                    with { Id = "group", Name = "Group" },
            ],
        };
}
