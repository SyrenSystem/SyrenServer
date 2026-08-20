using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MQTTnet;
using Syren.Server.Configuration;
using Syren.Server.Services;
using Xunit;

namespace Syren.Server.Tests;

public sealed class MqttServiceTests
{
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
        var distanceService = TestServices.CreateDistanceService(new RecordingSnapCastService());
        var mqttClient = new FakeMqttClientService();
        var options = Options.Create(new MqttOptions { AutoReconnect = false });
        var hostedService = new MqttHostedService(
            mqttClient,
            distanceService,
            options,
            new ServerSession(),
            NullLogger<MqttHostedService>.Instance
        );

        await hostedService.StartAsync(CancellationToken.None);

        Assert.Contains(
            "SyrenSystem/SyrenServer/GetSpeakerPosition/sensor",
            mqttClient.Cleared
        );
        Assert.Equal("SyrenSystem/SyrenServer/Status", mqttClient.Published.Last().Topic);
        Assert.True(mqttClient.Published.Last().Retain);
    }
}
