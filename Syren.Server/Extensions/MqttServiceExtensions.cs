using Syren.Server.Configuration;
using Syren.Server.Handlers;
using Syren.Server.Services;
using Microsoft.Extensions.Options;

namespace Syren.Server.Extensions;


public static class MqttServiceExtensions
{
    public static IServiceCollection AddMqttServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Configure MQTT options from appsettings
        services.AddOptions<MqttOptions>()
            .Bind(configuration.GetSection(MqttOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<MqttOptions>, MqttOptionsValidator>();

        // Register MQTT client service
        services.AddSingleton<ServerSession>();
        services.AddSingleton<ISyrenMqttClientFactory, SyrenMqttClientFactory>();
        services.AddSingleton<IMqttClientService, MqttClientService>();

        // Register handlers
        services.AddSingleton<IMqttMessageHandler, UpdateDistanceHandler>();
        services.AddSingleton<IMqttMessageHandler, SetSpeakerVolumeHandler>();
        services.AddSingleton<IMqttMessageHandler, ConnectSpeakerHandler>();
        services.AddSingleton<IMqttMessageHandler, DisconnectSpeakerHandler>();

        // Register hosted service for MQTT lifecycle management
        services.AddHostedService<MqttHostedService>();

        return services;
    }
}
