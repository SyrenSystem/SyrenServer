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

    [Fact]
    public async Task HandlersDropInvalidDistanceAndVolumeValues()
    {
        var snapCastService = new RecordingSnapCastService();
        var distanceService = TestServices.CreateDistanceService(snapCastService);
        var client = new FakeMqttClientService();
        var options = Options.Create(new MqttOptions());
        var connectHandler = new ConnectSpeakerHandler(
            distanceService,
            options,
            NullLogger<ConnectSpeakerHandler>.Instance
        );
        var distanceHandler = new UpdateDistanceHandler(
            distanceService,
            options,
            NullLogger<UpdateDistanceHandler>.Instance
        );
        var volumeHandler = new SetSpeakerVolumeHandler(
            distanceService,
            options,
            NullLogger<SetSpeakerVolumeHandler>.Instance
        );

        await connectHandler.HandleMessageAsync(
            Message(connectHandler.Topic, """{"id":"sensor","volume":101}"""),
            client
        );
        await distanceHandler.HandleMessageAsync(
            Message(distanceHandler.Topic, """{"id":"sensor","distance":-1}"""),
            client
        );
        await volumeHandler.HandleMessageAsync(
            Message(volumeHandler.Topic, """{"id":"sensor","volume":-1}"""),
            client
        );

        Assert.Empty(client.Published);
        Assert.Empty(snapCastService.VolumeChanges);
        Assert.Empty(await distanceService.GetConnectedSpeakerPositionsAsync());
    }

    private static MqttApplicationMessage Message(string topic, string payload) =>
        new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .Build();
}
