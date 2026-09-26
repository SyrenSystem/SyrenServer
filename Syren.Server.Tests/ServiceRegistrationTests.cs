using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Syren.Server.Configuration;
using Syren.Server.Extensions;
using Syren.Server.Handlers;
using Syren.Server.Services;
using Xunit;

namespace Syren.Server.Tests;

public sealed class ServiceRegistrationTests
{
    [Theory]
    [InlineData(true, 5)]
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
}
