using System.Numerics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MQTTnet;
using Syren.Server.Configuration;
using Syren.Server.Handlers;
using Syren.Server.Models;
using Syren.Server.Models.SnapCast;
using Syren.Server.Services;
using Xunit;

namespace Syren.Server.Tests;

public class RegressionTests
{
    [Fact]
    public async Task MqttHostedServiceRetriesUntilConnectionSucceeds()
    {
        var mqttClientService = new FakeMqttClientService(failuresBeforeSuccess: 2);
        var options = Options.Create(new MqttOptions
        {
            AutoReconnect = true,
            ReconnectDelaySeconds = 1,
        });
        var hostedService = new MqttHostedService(
            mqttClientService,
            options,
            NullLogger<MqttHostedService>.Instance
        );

        await hostedService.StartAsync(CancellationToken.None);
        await mqttClientService.Connected.Task.WaitAsync(TimeSpan.FromSeconds(4));
        await hostedService.StopAsync(CancellationToken.None);

        Assert.Equal(3, mqttClientService.ConnectionAttempts);
    }

    [Fact]
    public async Task MqttHostedServicePropagatesFailureWhenRetryIsDisabled()
    {
        var mqttClientService = new FakeMqttClientService(failuresBeforeSuccess: 1);
        var options = Options.Create(new MqttOptions { AutoReconnect = false });
        var hostedService = new MqttHostedService(
            mqttClientService,
            options,
            NullLogger<MqttHostedService>.Instance
        );

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            hostedService.StartAsync(CancellationToken.None)
        );
    }

    [Fact]
    public async Task MqttHostedServiceContinuesRetryingAfterDisconnection()
    {
        var mqttClientService = new FakeMqttClientService();
        var hostedService = new MqttHostedService(
            mqttClientService,
            Options.Create(new MqttOptions
            {
                AutoReconnect = true,
                ReconnectDelaySeconds = 1,
            }),
            NullLogger<MqttHostedService>.Instance
        );
        await hostedService.StartAsync(CancellationToken.None);
        await mqttClientService.Connected.Task.WaitAsync(TimeSpan.FromSeconds(1));

        mqttClientService.SimulateDisconnection(failuresBeforeSuccess: 2);
        await WaitUntilAsync(
            () => mqttClientService.IsConnected,
            TimeSpan.FromSeconds(5)
        );
        await hostedService.StopAsync(CancellationToken.None);

        Assert.Equal(4, mqttClientService.ConnectionAttempts);
    }

    [Fact]
    public async Task SetVolumeHandlerAwaitsTheVolumeChange()
    {
        var distanceService = new BlockingDistanceService();
        var handler = new SetSpeakerVolumeHandler(
            distanceService,
            Options.Create(new MqttOptions()),
            NullLogger<SetSpeakerVolumeHandler>.Instance
        );
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(handler.Topic)
            .WithPayload("""{"id":"sensor","volume":50}""")
            .Build();

        Task handlerTask = handler.HandleMessageAsync(
            message,
            new FakeMqttClientService()
        );

        Assert.False(handlerTask.IsCompleted);
        distanceService.VolumeChange.SetResult();
        await handlerTask;
    }

    [Fact]
    public async Task DisconnectHandlerAwaitsTheDisconnection()
    {
        var distanceService = new BlockingDistanceService();
        var handler = new DisconnectSpeakerHandler(
            distanceService,
            Options.Create(new MqttOptions()),
            NullLogger<DisconnectSpeakerHandler>.Instance
        );
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(handler.Topic)
            .WithPayload("""{"id":"sensor"}""")
            .Build();

        Task handlerTask = handler.HandleMessageAsync(
            message,
            new FakeMqttClientService()
        );

        Assert.False(handlerTask.IsCompleted);
        distanceService.Disconnection.SetResult();
        await handlerTask;
    }

    [Fact]
    public async Task DistanceServiceWaitsForStartupMuting()
    {
        var snapCastService = new RecordingSnapCastService(blockStartup: true);
        DistanceService distanceService = CreateDistanceService(snapCastService);

        Task startupTask = distanceService.StartAsync(CancellationToken.None);

        Assert.False(startupTask.IsCompleted);
        snapCastService.StartupMute.SetResult();
        await startupTask;
        Assert.Contains(("snap-client", 0), snapCastService.VolumeChanges);
    }

    [Fact]
    public async Task DistanceServiceUsesTheSmoothedDistanceForVolume()
    {
        var snapCastService = new RecordingSnapCastService();
        DistanceService distanceService = CreateDistanceService(snapCastService);
        await distanceService.StartAsync(CancellationToken.None);
        await distanceService.ConnectSpeakerAsync("sensor");

        await distanceService.UpdateDistanceAsync(new DistanceData
        {
            SpeakerId = "sensor",
            Distance = 100,
        });

        Assert.Equal(("snap-client", 50), snapCastService.VolumeChanges.Last());
    }

    [Fact]
    public async Task DistanceServicePropagatesStartupMuteFailure()
    {
        var snapCastService = new RecordingSnapCastService(failVolumeChanges: true);
        DistanceService distanceService = CreateDistanceService(snapCastService);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            distanceService.StartAsync(CancellationToken.None)
        );
    }

    private static DistanceService CreateDistanceService(ISnapCastService snapCastService)
    {
        var syrenSettings = Options.Create(new SyrenSettings
        {
            DistanceSmoothingFactor = 0.5,
        });
        var speakersOptions = Options.Create(new SpeakersOptions
        {
            SpeakersInfo =
            [
                new SpeakerInfo
                {
                    SensorId = "sensor",
                    SnapClientId = "snap-client",
                    FullVolumeDistance = 0,
                    MuteDistance = 100,
                },
            ],
        });

        return new DistanceService(
            syrenSettings,
            speakersOptions,
            snapCastService,
            NullLogger<DistanceService>.Instance
        );
    }

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        TimeSpan timeout
    )
    {
        using var cancellationTokenSource = new CancellationTokenSource(timeout);
        while (!condition())
        {
            await Task.Delay(25, cancellationTokenSource.Token);
        }
    }
}

internal sealed class FakeMqttClientService : IMqttClientService
{
    private int _failuresRemaining;

    public FakeMqttClientService(int failuresBeforeSuccess = 0)
    {
        _failuresRemaining = failuresBeforeSuccess;
    }

    public bool IsConnected { get; private set; }
    public int ConnectionAttempts { get; private set; }
    public TaskCompletionSource Connected { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );

    public Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        ConnectionAttempts++;
        if (_failuresRemaining-- > 0)
        {
            throw new InvalidOperationException("Broker unavailable");
        }

        IsConnected = true;
        Connected.TrySetResult();
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        IsConnected = false;
        return Task.CompletedTask;
    }

    public void SimulateDisconnection(int failuresBeforeSuccess)
    {
        IsConnected = false;
        _failuresRemaining = failuresBeforeSuccess;
    }

    public Task PublishAsync<T>(
        string topic,
        T message,
        CancellationToken cancellationToken = default
    ) => Task.CompletedTask;
}

internal sealed class BlockingDistanceService : IDistanceService
{
    public TaskCompletionSource VolumeChange { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );
    public TaskCompletionSource Disconnection { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );

    public Task UpdateDistanceAsync(DistanceData distance) => Task.CompletedTask;

    public Task UpdateDistancesAsync(IReadOnlyCollection<DistanceData> distances) =>
        Task.CompletedTask;

    public Task SetSpeakerVolumeAsync(string sensorId, double volume) => VolumeChange.Task;

    public Task<SpeakerState?> ConnectSpeakerAsync(string sensorId) =>
        Task.FromResult<SpeakerState?>(null);

    public Task DisconnectSpeakerAsync(string sensorId) => Disconnection.Task;

    public Vector3? GetUserPosition() => null;
}

internal sealed class RecordingSnapCastService : ISnapCastService
{
    private readonly bool _blockStartup;
    private readonly bool _failVolumeChanges;

    public RecordingSnapCastService(
        bool blockStartup = false,
        bool failVolumeChanges = false
    )
    {
        _blockStartup = blockStartup;
        _failVolumeChanges = failVolumeChanges;
    }

    public List<(string ClientId, int Percent)> VolumeChanges { get; } = [];
    public TaskCompletionSource StartupMute { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously
    );

    public Task<SystemStatus?> GetStatusAsync() => Task.FromResult<SystemStatus?>(null);

    public async Task SetClientVolumeAsync(string clientId, int percent)
    {
        if (_failVolumeChanges)
        {
            throw new HttpRequestException("SnapServer unavailable");
        }

        if (_blockStartup && percent == 0 && VolumeChanges.Count == 0)
        {
            await StartupMute.Task;
        }

        VolumeChanges.Add((clientId, percent));
    }

    public Task<double?> GetClientVolume(string clientId) => Task.FromResult<double?>(null);
}
