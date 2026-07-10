using Syren.Server.Configuration;
using Syren.Server.Services;

namespace Syren.Server.Extensions;

public static class DistanceServiceExtensions
{
    public static IServiceCollection AddDistanceServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<SyrenSettings>(configuration.GetSection(SyrenSettings.SectionName));
        services.Configure<SpeakersOptions>(configuration.GetSection(SpeakersOptions.SectionName));

        services.AddSingleton<DistanceService>();
        services.AddSingleton<IDistanceService>(serviceProvider =>
            serviceProvider.GetRequiredService<DistanceService>()
        );
        services.AddSingleton<IHostedService>(serviceProvider =>
            serviceProvider.GetRequiredService<DistanceService>()
        );

        return services;
    }
}
