using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Syren.Server.Configuration;
using Syren.Server.Extensions;
using Syren.Server.Handlers;
using Syren.Server.Models;
using Syren.Server.Services;
using Xunit;

namespace Syren.Server.Tests;

public sealed class ServiceRegistrationTests
{
    [Theory]
    [InlineData(true, 6)]
    [InlineData(false, 9)]
    public async Task ProfileModeIsChosenOnceWhenServicesAreRegistered(bool profileSessions, int handlerCount)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Playback:ProfileSessions"] = profileSessions.ToString(),
        }).Build();
        var snapcast = new RecordingSnapCastService();
        var store = new MemoryStateStore();
        TestServices.CreateDistanceService(snapcast, store,
            new SpeakerInfo { SensorId = "sensor", SnapClientId = "client", FullVolumeDistance = 0, MuteDistance = 100 });
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDistanceServices(configuration);
        services.AddMqttServices(configuration);
        services.AddSingleton<ISnapCastService>(snapcast);
        services.AddSingleton<ISystemStateStore>(store);

        Assert.Equal(handlerCount, services.Count(service => service.ServiceType == typeof(IMqttMessageHandler)));
        Assert.Equal(profileSessions, services.Any(service => service.ServiceType == typeof(IHostedService) &&
            service.ImplementationType == typeof(ProfilePlaybackPublisher)));

        using ServiceProvider provider = services.BuildServiceProvider();
        DistanceService distance = provider.GetRequiredService<DistanceService>();
        await distance.StartAsync(CancellationToken.None);
        if (profileSessions)
        {
            Assert.NotNull(provider.GetRequiredService<ProducerVolumeStore>());
            Assert.NotNull(provider.GetRequiredService<ProfilePositionService>());
            await Task.Delay(200);
            await distance.StopAsync(CancellationToken.None);
            Assert.Empty(snapcast.VolumeChanges);
        }
        else
        {
            await TestServices.WaitUntilAsync(() => snapcast.VolumeChanges.Contains(("client", 0)));
            await distance.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AirPlayIsOfferedOnlyWithProfileSessions(bool profileSessions)
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Playback:ProfileSessions"] = profileSessions.ToString(),
            ["Playback:Sources:0:Id"] = "spotify",
            ["Playback:Sources:0:Name"] = "Spotify",
            ["Playback:Sources:1:Id"] = "laptop",
            ["Playback:Sources:1:Name"] = "Laptop audio",
            ["Playback:Sources:2:Id"] = "casting",
            ["Playback:Sources:2:Name"] = "AirPlay",
        }).Build();
        var store = new MemoryStateStore();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDistanceServices(configuration);
        services.AddSingleton<ISnapCastService>(new RecordingSnapCastService());
        services.AddSingleton<ISystemStateStore>(store);
        using ServiceProvider provider = services.BuildServiceProvider();
        SystemConfigurationService configurationService = provider.GetRequiredService<SystemConfigurationService>();

        string[] expected = profileSessions ? ["spotify", "laptop", "casting"] : ["spotify", "laptop"];
        Assert.Equal(expected, configurationService.GetConfiguration().Sources.Select(source => source.Id));
        if (!profileSessions)
        {
            // The legacy group editor offers every published source, and Snapserver has no casting stream there.
            CommandResultMessage result = await configurationService.UpsertGroupAsync(new UpsertGroupCommand
            {
                RequestId = "group", ExpectedRevision = store.Current.Revision, Name = "Kantoor", SpeakerIds = [],
                SourcePriority = ["spotify", "casting"], VolumeMode = "manual", MasterVolume = 80, Muted = false,
            });
            Assert.False(result.Success);
            Assert.Equal("Playback group contains an unknown audio source", result.Error);
        }
    }
}
