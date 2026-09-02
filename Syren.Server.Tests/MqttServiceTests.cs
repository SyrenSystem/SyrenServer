using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MQTTnet;
using Syren.Server.Configuration;
using Syren.Server.Models;
using Syren.Server.Services;
using Xunit;

namespace Syren.Server.Tests;

public sealed class MqttServiceTests
{
    private const string PositionTopic = "SyrenSystem/SyrenServer/GetSpeakerPosition";
    private const string StatusTopic = "SyrenSystem/SyrenServer/Status";
    private const string ConfigurationTopic = "SyrenSystem/SyrenServer/Configuration";
    private const string RuntimeTopic = "SyrenSystem/SyrenServer/Runtime";

    [Fact]
    public void PartialSubscriptionGrantIsRejected()
    {
        bool granted = MqttSubscriptionValidator.AreAllGranted(
        [
            MqttClientSubscribeResultCode.GrantedQoS1,
            MqttClientSubscribeResultCode.NotAuthorized,
        ]);

        Assert.False(granted);
    }

    [Fact]
    public async Task HostedServiceSynchronizesPositionsBeforeOnlineStatus()
    {
        Stack stack = CreateStack(
            new MqttOptions { AutoReconnect = false },
            Speaker("sensor", "snap-client"),
            Speaker("other", "other-client")
        );
        await stack.DistanceService.ConnectSpeakerAsync("sensor", 50);

        await stack.HostedService.StartAsync(CancellationToken.None);

        Assert.Contains($"{PositionTopic}/other", stack.MqttClient.Cleared);
        var published = stack.MqttClient.Published;
        int positionIndex = IndexOf(published, $"{PositionTopic}/sensor");
        int statusIndex = IndexOf(published, StatusTopic);
        int configurationIndex = IndexOf(published, ConfigurationTopic);
        int runtimeIndex = IndexOf(published, RuntimeTopic);
        Assert.True(positionIndex >= 0);
        Assert.True(positionIndex < statusIndex);
        Assert.True(statusIndex < configurationIndex);
        Assert.True(configurationIndex < runtimeIndex);
        Assert.All(
            new[] { positionIndex, statusIndex, configurationIndex, runtimeIndex },
            index => Assert.True(published[index].Retain)
        );
        var status = Assert.IsType<ServerStatusMessage>(published[statusIndex].Message);
        Assert.True(status.Online);
        Assert.Equal(["sensor"], status.ConnectedSpeakerIds!);
        Assert.Equal(stack.StateStore.Current.StateId, status.StateId);
        var configuration = Assert.IsType<SystemConfigurationSnapshot>(published[configurationIndex].Message);
        Assert.Equal(stack.StateStore.Current.Revision, configuration.Revision);
        Assert.Equal(2, configuration.Speakers.Length);
        Assert.IsType<SystemRuntimeSnapshot>(published[runtimeIndex].Message);
    }

    [Fact]
    public async Task HostedServiceConnectsWhenSnapserverIsUnavailable()
    {
        Stack stack = CreateStack(new MqttOptions { AutoReconnect = false });
        stack.SnapCastService.StatusException = new SnapServerUnavailableException("down");

        await stack.HostedService.StartAsync(CancellationToken.None);

        Assert.True(stack.MqttClient.IsConnected);
        Assert.Equal(0, stack.SnapCastService.StatusCalls);
        var published = stack.MqttClient.Published;
        var status = Assert.IsType<ServerStatusMessage>(published[IndexOf(published, StatusTopic)].Message);
        Assert.True(status.Online);
        var runtime = Assert.IsType<SystemRuntimeSnapshot>(published[IndexOf(published, RuntimeTopic)].Message);
        Assert.False(runtime.SnapserverOnline);
    }

    [Fact]
    public async Task StateChangePublishesConfigurationOnce()
    {
        Stack stack = CreateStack(new MqttOptions { AutoReconnect = false });
        await stack.Publisher.StartAsync(CancellationToken.None);
        await stack.HostedService.StartAsync(CancellationToken.None);
        int initialCount = Count(stack.MqttClient.Published, ConfigurationTopic);

        CommandResultMessage first = await stack.ConfigurationService.SetSpeakerLevelAsync(new SetSpeakerLevelCommand
        {
            RequestId = "first",
            ExpectedRevision = stack.StateStore.Current.Revision,
            SpeakerId = "sensor",
            Level = 40,
        });
        CommandResultMessage second = await stack.ConfigurationService.SetSpeakerLevelAsync(new SetSpeakerLevelCommand
        {
            RequestId = "second",
            ExpectedRevision = first.Revision,
            SpeakerId = "sensor",
            Level = 60,
        });
        await TestServices.WaitUntilAsync(() => Count(stack.MqttClient.Published, ConfigurationTopic) > initialCount);
        await Task.Delay(300);
        await stack.Publisher.StopAsync(CancellationToken.None);

        Assert.True(second.Success);
        var configurations = stack.MqttClient.Published
            .Where(message => message.Topic == ConfigurationTopic)
            .Skip(initialCount)
            .Select(message => Assert.IsType<SystemConfigurationSnapshot>(message.Message))
            .ToArray();
        SystemConfigurationSnapshot configuration = Assert.Single(configurations);
        Assert.Equal(second.Revision, configuration.Revision);
        Assert.Equal(60, configuration.Speakers.Single().Level);
    }

    [Fact]
    public async Task ConnectPublishesUpdatedStatus()
    {
        Stack stack = CreateStack(
            new MqttOptions { AutoReconnect = false },
            Speaker("sensor", "snap-client"),
            Speaker("other", "other-client")
        );
        await stack.DistanceService.ConnectSpeakerAsync("sensor", 50);
        await stack.Publisher.StartAsync(CancellationToken.None);
        await stack.HostedService.StartAsync(CancellationToken.None);
        int initialCount = Count(stack.MqttClient.Published, StatusTopic);

        await stack.DistanceService.ConnectSpeakerAsync("other", null);
        await TestServices.WaitUntilAsync(() => Count(stack.MqttClient.Published, StatusTopic) > initialCount);
        ServerStatusMessage afterConnect = LastStatus(stack);
        int afterConnectCount = Count(stack.MqttClient.Published, StatusTopic);

        await stack.DistanceService.DisconnectSpeakerAsync("other");
        await TestServices.WaitUntilAsync(() => Count(stack.MqttClient.Published, StatusTopic) > afterConnectCount);
        ServerStatusMessage afterDisconnect = LastStatus(stack);
        await stack.Publisher.StopAsync(CancellationToken.None);

        Assert.True(afterConnect.Online);
        Assert.Equal(["other", "sensor"], afterConnect.ConnectedSpeakerIds!);
        Assert.True(afterDisconnect.Online);
        Assert.Equal(["sensor"], afterDisconnect.ConnectedSpeakerIds!);
        Assert.All(
            stack.MqttClient.Published.Where(message => message.Topic == StatusTopic),
            message => Assert.True(message.Retain)
        );
    }

    [Fact]
    public async Task RetiredSensorIsClearedOnStateChange()
    {
        Stack stack = CreateStack(new MqttOptions { AutoReconnect = false });
        await stack.DistanceService.ConnectSpeakerAsync("sensor", 50);
        await stack.Publisher.StartAsync(CancellationToken.None);
        await stack.HostedService.StartAsync(CancellationToken.None);
        int initialCount = Count(stack.MqttClient.Published, ConfigurationTopic);

        CommandResultMessage result = await stack.ConfigurationService.DeleteSpeakerAsync(new DeleteSpeakerCommand
        {
            RequestId = "delete",
            ExpectedRevision = stack.StateStore.Current.Revision,
            SpeakerId = "sensor",
        });
        await TestServices.WaitUntilAsync(() => stack.MqttClient.Cleared.Contains($"{PositionTopic}/sensor"));
        await TestServices.WaitUntilAsync(() => stack.StateStore.Current.RetiredSensorIds.Count == 0);
        await TestServices.WaitUntilAsync(() => Count(stack.MqttClient.Published, ConfigurationTopic) > initialCount);
        await Task.Delay(300);
        await stack.Publisher.StopAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Empty(await stack.DistanceService.GetRetiredSpeakerIdsAsync());
        Assert.Equal(result.Revision, stack.StateStore.Current.Revision);
        var configurations = stack.MqttClient.Published
            .Where(message => message.Topic == ConfigurationTopic)
            .Skip(initialCount)
            .Select(message => Assert.IsType<SystemConfigurationSnapshot>(message.Message))
            .ToArray();
        SystemConfigurationSnapshot configuration = Assert.Single(configurations);
        Assert.Equal(stack.StateStore.Current.Revision, configuration.Revision);
        Assert.Empty(configuration.Speakers);
    }

    [Fact]
    public async Task NoPeriodicConfigurationRepublish()
    {
        Stack stack = CreateStack(new MqttOptions { AutoReconnect = true, ReconnectDelaySeconds = 1 });
        await stack.Publisher.StartAsync(CancellationToken.None);

        await stack.HostedService.StartAsync(CancellationToken.None);
        await TestServices.WaitUntilAsync(() => Count(stack.MqttClient.Published, ConfigurationTopic) >= 1);
        await Task.Delay(2500);
        await stack.HostedService.StopAsync(CancellationToken.None);
        await stack.Publisher.StopAsync(CancellationToken.None);

        Assert.Equal(1, Count(stack.MqttClient.Published, ConfigurationTopic));
        Assert.Equal(1, Count(stack.MqttClient.Published, RuntimeTopic));
    }

    private static Stack CreateStack(MqttOptions mqttOptions, params SpeakerInfo[] speakers)
    {
        var snapCastService = new RecordingSnapCastService();
        var stateStore = new MemoryStateStore();
        DistanceService distanceService = TestServices.CreateDistanceService(snapCastService, stateStore, speakers);
        SystemConfigurationService configurationService = TestServices.CreateConfigurationService(
            stateStore,
            distanceService,
            snapCastService
        );
        var mqttClient = new FakeMqttClientService();
        IOptions<MqttOptions> options = Options.Create(mqttOptions);
        var session = new ServerSession();
        var publisher = new ConfigurationPublisher(
            mqttClient,
            distanceService,
            configurationService,
            stateStore,
            options,
            session,
            NullLogger<ConfigurationPublisher>.Instance
        );
        var hostedService = new MqttHostedService(
            mqttClient,
            distanceService,
            publisher,
            options,
            session,
            NullLogger<MqttHostedService>.Instance
        );
        return new Stack(
            stateStore,
            snapCastService,
            distanceService,
            configurationService,
            mqttClient,
            publisher,
            hostedService
        );
    }

    private static SpeakerInfo Speaker(string sensorId, string snapClientId) => new()
    {
        SensorId = sensorId,
        SnapClientId = snapClientId,
        FullVolumeDistance = 0,
        MuteDistance = 100,
    };

    private static int IndexOf(
        IReadOnlyList<(string Topic, object Message, bool Retain, MQTTnet.Protocol.MqttQualityOfServiceLevel Quality)> published,
        string topic)
    {
        for (int index = 0; index < published.Count; index++)
        {
            if (published[index].Topic == topic)
            {
                return index;
            }
        }
        return -1;
    }

    private static int Count(
        IReadOnlyList<(string Topic, object Message, bool Retain, MQTTnet.Protocol.MqttQualityOfServiceLevel Quality)> published,
        string topic) => published.Count(message => message.Topic == topic);

    private static ServerStatusMessage LastStatus(Stack stack) => Assert.IsType<ServerStatusMessage>(
        stack.MqttClient.Published.Last(message => message.Topic == StatusTopic).Message
    );

    private sealed record Stack(
        MemoryStateStore StateStore,
        RecordingSnapCastService SnapCastService,
        DistanceService DistanceService,
        SystemConfigurationService ConfigurationService,
        FakeMqttClientService MqttClient,
        ConfigurationPublisher Publisher,
        MqttHostedService HostedService);
}
