using Syren.Server.Configuration;
using Syren.Server.Services;

namespace Syren.Server.Extensions;

public static class DistanceServiceExtensions
{
    public static IServiceCollection AddDistanceServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<SpeakersOptions>(configuration.GetSection(SpeakersOptions.SectionName));

        services.AddSingleton<IDistanceService, DistanceService>();

        return services;
    }
}
