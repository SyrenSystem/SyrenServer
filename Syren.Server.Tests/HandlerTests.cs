using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MQTTnet;
using Syren.Server.Configuration;
using Syren.Server.Handlers;
using Syren.Server.Models;
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

        await handler.HandleMessageAsync(Message(handler.Topic, """{"id":"sensor","volume":25}"""), client);

        Assert.Contains(client.Published, publish =>
            publish.Topic.EndsWith("/sensor") && publish.Retain);
    }

    [Fact]
    public async Task ConnectWithoutVolumeKeepsStoredLevel()
    {
        var stateStore = new MemoryStateStore(new PersistentSystemState
        {
            StateId = Guid.NewGuid().ToString(),
            Speakers =
            [
                new PersistentSpeakerState
                {
                    SpeakerId = "sensor",
                    Name = "Sensor",
                    SensorId = "sensor",
                    SnapClientId = "snap-client",
                    FullVolumeDistance = 0,
                    MuteDistance = 100,
                    Connected = false,
                    Volume = 35,
                },
            ],
        });
        var snapCastService = new RecordingSnapCastService();
        var distanceService = TestServices.CreateDistanceService(snapCastService, stateStore);
        var client = new FakeMqttClientService();
        var handler = new ConnectSpeakerHandler(
            distanceService,
            Options.Create(new MqttOptions()),
            NullLogger<ConnectSpeakerHandler>.Instance
        );

        await handler.HandleMessageAsync(Message(handler.Topic, """{"id":"sensor"}"""), client);

        Assert.Equal(("snap-client", 35), Assert.Single(snapCastService.VolumeChanges));
        Assert.Equal(35, stateStore.Current.Speakers.Single().Volume);
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

        await handler.HandleMessageAsync(Message(handler.Topic, """{"id":"sensor"}"""), client);

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

    [Fact]
    public async Task ConfigurationHandlerPublishesResultOnly()
    {
        var stateStore = new MemoryStateStore(new PersistentSystemState
        {
            StateId = "state",
            Revision = 2,
            Speakers =
            [
                new PersistentSpeakerState
                {
                    SpeakerId = "speaker-one",
                    Name = "Kitchen speaker",
                    SnapClientId = "snap-one",
                    Connected = false,
                    Volume = 100,
                },
            ],
        });
        SystemConfigurationService configurationService = TestServices.CreateConfigurationService(
            stateStore,
            new RecordingDistanceService(stateStore),
            new RecordingSnapCastService()
        );
        var client = new FakeMqttClientService();
        var handler = new SetSpeakerLevelHandler(
            configurationService,
            Options.Create(new MqttOptions()),
            NullLogger<SetSpeakerLevelHandler>.Instance
        );

        await handler.HandleMessageAsync(
            Message(
                handler.Topic,
                """{"requestId":"request","expectedRevision":2,"speakerId":"speaker-one","level":30}"""
            ),
            client
        );

        var published = Assert.Single(client.Published);
        Assert.Equal("SyrenSystem/SyrenServer/CommandResult/request", published.Topic);
        var result = Assert.IsType<CommandResultMessage>(published.Message);
        Assert.True(result.Success);
        Assert.Equal(3, result.Revision);
        Assert.Equal(30, stateStore.Current.Speakers.Single().Volume);
    }

    private static MqttApplicationMessage Message(string topic, string payload) =>
        new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .Build();
}
