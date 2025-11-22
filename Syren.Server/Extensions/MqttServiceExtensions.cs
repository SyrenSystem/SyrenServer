using Syren.Server.Configuration;
using Syren.Server.Handlers;
using Syren.Server.Services;

namespace Syren.Server.Extensions;


public static class MqttServiceExtensions
{
    public static IServiceCollection AddMqttServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Configure MQTT options from appsettings
        services.Configure<MqttOptions>(configuration.GetSection(MqttOptions.SectionName));

        // Register MQTT client service
        services.AddSingleton<IMqttClientService, MqttClientService>();

        // Register handlers
        services.AddSingleton<IMqttMessageHandler, UpdateDistancesHandler>();
        services.AddSingleton<IMqttMessageHandler, AddSpeakerHandler>();

        // Register hosted service for MQTT lifecycle management
        services.AddHostedService<MqttHostedService>();

        return services;
    }
}
