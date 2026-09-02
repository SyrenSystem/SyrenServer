using Syren.Server.Configuration;
using Syren.Server.Models;
using Syren.Server.Models.SnapCast;
using Syren.Server.Services;
using Xunit;

namespace Syren.Server.Tests;

public sealed class DistanceServiceTests
{
    [Fact]
    public async Task StartupContinuesWhenOneSnapclientIsAbsent()
    {
        var snapCastService = new RecordingSnapCastService();
        snapCastService.FailingClientIds.Add("missing-client");
        DistanceService service = TestServices.CreateDistanceService(
            snapCastService,
            null,
            Speaker("one", "missing-client"),
            Speaker("two", "working-client")
        );

        await service.StartAsync(CancellationToken.None);
        await TestServices.WaitUntilAsync(() => snapCastService.VolumeChanges.Contains(("working-client", 0)));

        Assert.Contains(("working-client", 0), snapCastService.VolumeChanges);
    }

    [Fact]
    public async Task UpdateDistanceForUnconnectedSpeakerDoesNotThrow()
    {
        var snapCastService = new RecordingSnapCastService();
        DistanceService service = TestServices.CreateDistanceService(snapCastService);

        await service.UpdateDistanceAsync(new DistanceData { SpeakerId = "sensor", Distance = 25 });

        Assert.Empty(snapCastService.VolumeChanges);
    }

    [Fact]
    public async Task ConnectUsesGivenVolumeAndRepeatedDistanceSkipsRpc()
    {
        var snapCastService = new RecordingSnapCastService();
        DistanceService service = TestServices.CreateDistanceService(snapCastService);
        await service.ConnectSpeakerAsync("sensor", 40);
        await service.UpdateDistanceAsync(new DistanceData { SpeakerId = "sensor", Distance = 50 });
        await TestServices.WaitUntilAsync(() => snapCastService.VolumeChanges.Count >= 2);
        int count = snapCastService.VolumeChanges.Count;

        await service.UpdateDistanceAsync(new DistanceData { SpeakerId = "sensor", Distance = 50 });
        await Task.Delay(50);

        Assert.Equal(("snap-client", 40), snapCastService.VolumeChanges[0]);
        Assert.Equal(("snap-client", 20), snapCastService.VolumeChanges[1]);
        Assert.Equal(count, snapCastService.VolumeChanges.Count);
    }

    [Fact]
    public async Task RestoredSpeakerKeepsPositionAndWaitsForFreshDistance()
    {
        var stateStore = new MemoryStateStore(RestoredState());
        var snapCastService = new RecordingSnapCastService();
        DistanceService service = TestServices.CreateDistanceService(snapCastService, stateStore);

        IReadOnlyList<SpeakerPosition> positions = await service.GetConnectedSpeakerPositionsAsync();

        Assert.Single(positions);
        Assert.Equal(2, positions[0].Position.Y);
        Assert.Empty(snapCastService.VolumeChanges);
    }

    [Fact]
    public async Task RestoredSpeakerStaysMutedUntilFreshDistance()
    {
        var stateStore = new MemoryStateStore(RestoredState());
        var snapCastService = new RecordingSnapCastService();
        DistanceService service = TestServices.CreateDistanceService(snapCastService, stateStore);

        await service.ApplyCurrentVolumesAsync();
        await TestServices.WaitUntilAsync(() => snapCastService.VolumeChanges.Count >= 1);
        await service.UpdateDistanceAsync(new DistanceData { SpeakerId = "sensor", Distance = 0 });
        await TestServices.WaitUntilAsync(() => snapCastService.VolumeChanges.Count >= 2);

        Assert.Equal(("snap-client", 0), snapCastService.VolumeChanges[0]);
        Assert.Equal(("snap-client", 35), snapCastService.VolumeChanges[1]);
    }

    [Fact]
    public async Task ReconnectOfRestoredSpeakerStaysMutedUntilFreshDistance()
    {
        var stateStore = new MemoryStateStore(RestoredState());
        var snapCastService = new RecordingSnapCastService();
        DistanceService service = TestServices.CreateDistanceService(snapCastService, stateStore);

        await service.ConnectSpeakerAsync("sensor", null);
        await service.ApplyCurrentVolumesAsync();
        await Task.Delay(50);
        Assert.Equal(("snap-client", 0), Assert.Single(snapCastService.VolumeChanges));

        await service.UpdateDistanceAsync(new DistanceData { SpeakerId = "sensor", Distance = 0 });
        await TestServices.WaitUntilAsync(() => snapCastService.VolumeChanges.Count >= 2);

        Assert.Equal(("snap-client", 35), snapCastService.VolumeChanges[1]);
    }

    [Fact]
    public async Task ConnectWithoutVolumeUsesStoredLevel()
    {
        var stateStore = new MemoryStateStore(RestoredState(connected: false));
        var snapCastService = new RecordingSnapCastService();
        DistanceService service = TestServices.CreateDistanceService(snapCastService, stateStore);

        SpeakerState? state = await service.ConnectSpeakerAsync("sensor", null);

        Assert.NotNull(state);
        Assert.Equal(("snap-client", 35), Assert.Single(snapCastService.VolumeChanges));
        Assert.Equal(35, stateStore.Current.Speakers.Single().Volume);
        Assert.True(stateStore.Current.Speakers.Single().Connected);
    }

    [Fact]
    public async Task DispatcherCoalescesBurstAndSendsLatest()
    {
        var snapCastService = new RecordingSnapCastService();
        DistanceService service = TestServices.CreateDistanceService(snapCastService);
        await service.ConnectSpeakerAsync("sensor", 100);
        snapCastService.Blocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await service.UpdateDistanceAsync(new DistanceData { SpeakerId = "sensor", Distance = 10 });
        await service.UpdateDistanceAsync(new DistanceData { SpeakerId = "sensor", Distance = 20 });
        await service.UpdateDistanceAsync(new DistanceData { SpeakerId = "sensor", Distance = 30 });
        snapCastService.Blocker.SetResult();
        await TestServices.WaitUntilAsync(() => snapCastService.VolumeChanges.Count >= 3);
        await Task.Delay(50);

        Assert.Equal(
            [("snap-client", 100), ("snap-client", 90), ("snap-client", 70)],
            snapCastService.VolumeChanges
        );
    }

    [Fact]
    public async Task ApplyCurrentVolumesSkipsUnchangedVolumes()
    {
        var snapCastService = new RecordingSnapCastService();
        DistanceService service = TestServices.CreateDistanceService(snapCastService);
        await service.ConnectSpeakerAsync("sensor", 40);

        await service.ApplyCurrentVolumesAsync();
        await service.ApplyCurrentVolumesAsync();
        await Task.Delay(50);

        Assert.Equal(("snap-client", 40), Assert.Single(snapCastService.VolumeChanges));
    }

    [Fact]
    public async Task ApplyCurrentVolumesSkipsClientsUnknownToSnapserver()
    {
        var snapCastService = new RecordingSnapCastService();
        DistanceService service = TestServices.CreateDistanceService(snapCastService);
        await service.ConnectSpeakerAsync("sensor", 40);
        snapCastService.VolumeChanges.Clear();

        await service.ApplyCurrentVolumesAsync(new Dictionary<string, SnapClientVolumeStatus?>());
        await Task.Delay(50);

        Assert.Empty(snapCastService.VolumeChanges);
    }

    [Fact]
    public async Task ApplyCurrentVolumesSkipsWhenSnapserverAlreadyReportsTarget()
    {
        var snapCastService = new RecordingSnapCastService();
        DistanceService service = TestServices.CreateDistanceService(snapCastService);
        await service.ConnectSpeakerAsync("sensor", 40);
        snapCastService.VolumeChanges.Clear();
        var reportedVolumes = new Dictionary<string, SnapClientVolumeStatus?>
        {
            ["snap-client"] = new SnapClientVolumeStatus { Percent = 40, Muted = false },
        };

        await service.ApplyCurrentVolumesAsync(reportedVolumes);
        await Task.Delay(50);

        Assert.Empty(snapCastService.VolumeChanges);
    }

    [Fact]
    public async Task ApplyCurrentVolumesResendsWhenSnapserverReportsDifferentVolume()
    {
        var snapCastService = new RecordingSnapCastService();
        DistanceService service = TestServices.CreateDistanceService(snapCastService);
        await service.ConnectSpeakerAsync("sensor", 40);
        snapCastService.VolumeChanges.Clear();
        var reportedVolumes = new Dictionary<string, SnapClientVolumeStatus?>
        {
            ["snap-client"] = new SnapClientVolumeStatus { Percent = 0, Muted = false },
        };

        await service.ApplyCurrentVolumesAsync(reportedVolumes);
        await TestServices.WaitUntilAsync(() => snapCastService.VolumeChanges.Count >= 1);

        Assert.Equal(("snap-client", 40), Assert.Single(snapCastService.VolumeChanges));
    }

    [Fact]
    public async Task ManualGroupUsesLevelTimesMaster()
    {
        var stateStore = new MemoryStateStore(new PersistentSystemState
        {
            StateId = Guid.NewGuid().ToString(),
            Speakers =
            [
                RestoredSpeaker(connected: false) with { Volume = 80 },
                new PersistentSpeakerState
                {
                    SpeakerId = "desk",
                    Name = "Desk",
                    SnapClientId = "desk-client",
                    Connected = false,
                    Volume = 60,
                },
            ],
            Groups =
            [
                PersistentStateFactory.CreateDefaultGroup(["sensor", "desk"], TestServices.SourceIds, "manual")
                    with { MasterVolume = 50 },
            ],
        });
        var snapCastService = new RecordingSnapCastService();
        DistanceService service = TestServices.CreateDistanceService(snapCastService, stateStore);

        await service.ApplyCurrentVolumesAsync();
        await TestServices.WaitUntilAsync(() => snapCastService.VolumeChanges.Count >= 2);

        Assert.Contains(("snap-client", 40), snapCastService.VolumeChanges);
        Assert.Contains(("desk-client", 30), snapCastService.VolumeChanges);
    }

    [Fact]
    public async Task AutomaticGroupMemberWithoutCalibrationIsSilent()
    {
        var stateStore = new MemoryStateStore(RestoredState(connected: false));
        var snapCastService = new RecordingSnapCastService();
        DistanceService service = TestServices.CreateDistanceService(snapCastService, stateStore);

        await service.ApplyCurrentVolumesAsync();
        await TestServices.WaitUntilAsync(() => snapCastService.VolumeChanges.Count >= 1);

        Assert.Equal(("snap-client", 0), Assert.Single(snapCastService.VolumeChanges));
    }

    [Fact]
    public async Task ApplyConfigurationChangeRetiresRemovedSensor()
    {
        var stateStore = new MemoryStateStore();
        var snapCastService = new RecordingSnapCastService();
        DistanceService service = TestServices.CreateDistanceService(snapCastService, stateStore);
        await service.ConnectSpeakerAsync("sensor", 40);

        service.ApplyConfigurationChange(current => current with
        {
            Revision = current.Revision + 1,
            Speakers = [],
            Groups = current.Groups.Select(group => group with { SpeakerIds = [] }).ToList(),
        });

        Assert.Equal(["sensor"], await service.GetRetiredSpeakerIdsAsync());
        Assert.Equal(["sensor"], stateStore.Current.RetiredSensorIds);
        Assert.Empty(await service.GetConnectedSpeakerPositionsAsync());
        Assert.Empty(await service.GetConfiguredSpeakerIdsAsync());

        long revisionBeforeConfirm = stateStore.Current.Revision;
        await service.ConfirmRetiredSpeakerIdsClearedAsync(["sensor"]);

        Assert.Empty(stateStore.Current.RetiredSensorIds);
        Assert.Equal(revisionBeforeConfirm, stateStore.Current.Revision);
    }

    [Fact]
    public async Task ApplyConfigurationChangeLevelSurvivesLaterSave()
    {
        var stateStore = new MemoryStateStore();
        var snapCastService = new RecordingSnapCastService();
        DistanceService service = TestServices.CreateDistanceService(snapCastService, stateStore);

        service.ApplyConfigurationChange(current => current with
        {
            Revision = current.Revision + 1,
            Speakers = current.Speakers.Select(speaker => speaker with { Volume = 55 }).ToList(),
        });
        await service.ConnectSpeakerAsync("sensor", null);

        Assert.Equal(("snap-client", 55), Assert.Single(snapCastService.VolumeChanges));
        Assert.Equal(55, stateStore.Current.Speakers.Single().Volume);
    }

    [Fact]
    public async Task DisconnectRemovesStateWhenSnapcastFails()
    {
        var snapCastService = new RecordingSnapCastService();
        DistanceService service = TestServices.CreateDistanceService(snapCastService);
        await service.ConnectSpeakerAsync("sensor", 20);
        snapCastService.FailingClientIds.Add("snap-client");

        DisconnectResult result = await service.DisconnectSpeakerAsync("sensor");

        Assert.Equal(DisconnectResult.Disconnected, result);
        Assert.Empty(await service.GetConnectedSpeakerPositionsAsync());
    }

    [Fact]
    public async Task ThirdSpeakerConnectIsRejectedWhenGeometryIsDegenerate()
    {
        var snapCastService = new RecordingSnapCastService();
        DistanceService service = TestServices.CreateDistanceService(
            snapCastService,
            null,
            Speaker("one", "client-one"),
            Speaker("two", "client-two"),
            Speaker("three", "client-three")
        );
        await service.ConnectSpeakerAsync("one", 20);
        await service.ConnectSpeakerAsync("two", 20);
        await service.UpdateDistanceAsync(new DistanceData { SpeakerId = "one", Distance = 50 });
        await service.UpdateDistanceAsync(new DistanceData { SpeakerId = "two", Distance = 50 });

        SpeakerState? third = await service.ConnectSpeakerAsync("three", 20);

        Assert.Null(third);
        Assert.Equal(2, (await service.GetConnectedSpeakerPositionsAsync()).Count);
    }

    [Fact]
    public async Task ThirdSpeakerPlacementIsFinite()
    {
        var snapCastService = new RecordingSnapCastService();
        DistanceService service = TestServices.CreateDistanceService(
            snapCastService,
            null,
            Speaker("one", "client-one"),
            Speaker("two", "client-two"),
            Speaker("three", "client-three")
        );
        await service.ConnectSpeakerAsync("one", 20);
        await service.UpdateDistanceAsync(new DistanceData { SpeakerId = "one", Distance = 100 });
        await service.ConnectSpeakerAsync("two", 20);
        await service.UpdateDistanceAsync(new DistanceData { SpeakerId = "one", Distance = 80 });
        await service.UpdateDistanceAsync(new DistanceData { SpeakerId = "two", Distance = 60 });

        SpeakerState? third = await service.ConnectSpeakerAsync("three", 20);

        SpeakerState state = Assert.IsType<SpeakerState>(third);
        Assert.True(float.IsFinite(state.Position.X));
        Assert.True(float.IsFinite(state.Position.Y));
        Assert.True(float.IsFinite(state.Position.Z));
    }

    [Fact]
    public void SpeakersOptionsRejectSwappedDistances()
    {
        var options = new SpeakersOptions
        {
            SpeakersInfo = [Speaker("sensor", "client", fullDistance: 100, muteDistance: 50)],
        };

        Assert.True(new SpeakersOptionsValidator().Validate(null, options).Failed);
    }

    private static SpeakerInfo Speaker(
        string sensorId,
        string clientId,
        double fullDistance = 0,
        double muteDistance = 100) => new()
    {
        SensorId = sensorId,
        SnapClientId = clientId,
        FullVolumeDistance = fullDistance,
        MuteDistance = muteDistance,
    };

    private static PersistentSpeakerState RestoredSpeaker(bool connected = true) => new()
    {
        SpeakerId = "sensor",
        Name = "Sensor",
        SensorId = "sensor",
        SnapClientId = "snap-client",
        FullVolumeDistance = 0,
        MuteDistance = 100,
        Connected = connected,
        Volume = 35,
        Position = connected ? new PositionVector { X = 1, Y = 2, Z = 3 } : null,
    };

    private static PersistentSystemState RestoredState(bool connected = true) => new()
    {
        StateId = Guid.NewGuid().ToString(),
        Speakers = [RestoredSpeaker(connected)],
    };
}
