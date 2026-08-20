using Syren.Server.Configuration;
using Syren.Server.Services;
using Microsoft.Extensions.Options;

namespace Syren.Server.Extensions;

public static class SnapCastExtensions
{
    public static IServiceCollection AddSnapCastServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<SnapCastOptions>()
            .Bind(configuration.GetSection(SnapCastOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<SnapCastOptions>, SnapCastOptionsValidator>();
        services.AddHttpClient<ISnapCastService, SnapCastService>((serviceProvider, client) =>
        {
            SnapCastOptions options = serviceProvider
                .GetRequiredService<IOptions<SnapCastOptions>>()
                .Value;
            client.BaseAddress = new Uri($"http://{options.ServerHost}:{options.HttpPort}");
            client.Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds);
        });

        return services;
    }
}
