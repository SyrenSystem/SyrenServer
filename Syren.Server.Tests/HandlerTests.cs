using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MQTTnet;
using Syren.Server.Configuration;
using Syren.Server.Handlers;
using Syren.Server.Services;
using Xunit;

namespace Syren.Server.Tests;

public sealed class HandlerTests
{
    [Fact]
    public async Task ConnectPublishesRetainedPosition()
    {
        var distanceService = TestServices.CreateDistanceService(new RecordingSnapCastService());
        var client = new FakeMqttClientService();
        var handler = new ConnectSpeakerHandler(
            distanceService,
            Options.Create(new MqttOptions()),
            NullLogger<ConnectSpeakerHandler>.Instance
        );
        MqttApplicationMessage message = new MqttApplicationMessageBuilder()
            .WithTopic(handler.Topic)
            .WithPayload("""{"id":"sensor","volume":25}""")
            .Build();

        await handler.HandleMessageAsync(message, client);

        Assert.Contains(client.Published, publish =>
            publish.Topic.EndsWith("/sensor") && publish.Retain);
    }

    [Fact]
    public async Task DisconnectClearsAlreadyDisconnectedSpeaker()
    {
        var distanceService = TestServices.CreateDistanceService(new RecordingSnapCastService());
        var client = new FakeMqttClientService();
        var handler = new DisconnectSpeakerHandler(
            distanceService,
            Options.Create(new MqttOptions()),
            NullLogger<DisconnectSpeakerHandler>.Instance
        );
        MqttApplicationMessage message = new MqttApplicationMessageBuilder()
            .WithTopic(handler.Topic)
            .WithPayload("""{"id":"sensor"}""")
            .Build();

        await handler.HandleMessageAsync(message, client);

        Assert.Contains("SyrenSystem/SyrenServer/GetSpeakerPosition/sensor", client.Cleared);
    }
}
