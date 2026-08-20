using Microsoft.Extensions.Options;
using Syren.Server.Configuration;
using Syren.Server.Models;
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
    public async Task ConnectUsesRequiredVolumeAndRepeatedDistanceSkipsRpc()
    {
        var snapCastService = new RecordingSnapCastService();
        DistanceService service = TestServices.CreateDistanceService(snapCastService);
        await service.ConnectSpeakerAsync("sensor", 40);
        await service.UpdateDistanceAsync(new DistanceData { SpeakerId = "sensor", Distance = 50 });
        await WaitUntilAsync(() => snapCastService.VolumeChanges.Count >= 2);
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
        var stateStore = new MemoryStateStore(new PersistentSystemState
        {
            StateId = Guid.NewGuid().ToString(),
            Speakers =
            [
                new PersistentSpeakerState
                {
                    SensorId = "sensor",
                    Connected = true,
                    Volume = 35,
                    Position = new PositionVector { X = 1, Y = 2, Z = 3 },
                },
            ],
        });
        var snapCastService = new RecordingSnapCastService();
        DistanceService service = TestServices.CreateDistanceService(snapCastService, stateStore);

        IReadOnlyList<SpeakerPosition> positions = await service.GetConnectedSpeakerPositionsAsync();

        Assert.Single(positions);
        Assert.Equal(2, positions[0].Position.Y);
        Assert.Empty(snapCastService.VolumeChanges);
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

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(10, cancellationTokenSource.Token);
        }
    }
}
