using Syren.Server.Configuration;
using Syren.Server.Services;
using Microsoft.Extensions.Options;

namespace Syren.Server.Extensions;

public static class DistanceServiceExtensions
{
    public static IServiceCollection AddDistanceServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<SpeakersOptions>()
            .Bind(configuration.GetSection(SpeakersOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<SpeakersOptions>, SpeakersOptionsValidator>();
        services.AddOptions<StateOptions>()
            .Bind(configuration.GetSection(StateOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<StateOptions>, StateOptionsValidator>();
        services.AddSingleton<ISystemStateStore, SystemStateStore>();

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
