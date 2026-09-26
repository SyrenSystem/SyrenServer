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
        services.AddOptions<PlaybackOptions>()
            .Bind(configuration.GetSection(PlaybackOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<PlaybackOptions>, PlaybackOptionsValidator>();
        services.AddSingleton<ISystemStateStore, SystemStateStore>();

        // Profile mode is chosen once here, so legacy services can never set physical client volumes in version 3.
        bool profileSessions = ProfileSessions(configuration);
        services.AddSingleton(provider => profileSessions
            ? ActivatorUtilities.CreateInstance<DistanceService>(provider, LegacySnapCast(provider))
            : ActivatorUtilities.CreateInstance<DistanceService>(provider));
        services.AddSingleton<IDistanceService>(serviceProvider =>
            serviceProvider.GetRequiredService<DistanceService>()
        );
        services.AddSingleton<IHostedService>(serviceProvider =>
            serviceProvider.GetRequiredService<DistanceService>()
        );
        services.AddSingleton(provider => profileSessions
            ? ActivatorUtilities.CreateInstance<SystemConfigurationService>(provider, LegacySnapCast(provider))
            : ActivatorUtilities.CreateInstance<SystemConfigurationService>(provider));
        services.AddSingleton<ISystemConfigurationService>(serviceProvider =>
            serviceProvider.GetRequiredService<SystemConfigurationService>()
        );
        services.AddSingleton<IHostedService>(serviceProvider =>
            serviceProvider.GetRequiredService<SystemConfigurationService>()
        );

        services.AddHostedService<SnapCastEventsService>();

        if (!profileSessions)
        {
            return services;
        }
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<SessionCatalogueService>();
        services.AddSingleton<ProfileConfigurationService>();
        services.AddSingleton<ProfilePositionService>();
        services.AddSingleton<PcSessionService>();
        services.AddHostedService(provider => provider.GetRequiredService<PcSessionService>());
        services.AddSingleton<ProfilePlaybackCoordinator>();
        services.AddSingleton<SessionTransportBindings>();
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<SessionTransportBindings>());
        services.AddHostedService<ProfilePlaybackPublisher>();

        return services;
    }

    public static bool ProfileSessions(IConfiguration configuration) =>
        configuration.GetValue<bool>($"{PlaybackOptions.SectionName}:{nameof(PlaybackOptions.ProfileSessions)}");

    private static ReadOnlySnapCastService LegacySnapCast(IServiceProvider provider) =>
        new(provider.GetRequiredService<ISnapCastService>());
}
