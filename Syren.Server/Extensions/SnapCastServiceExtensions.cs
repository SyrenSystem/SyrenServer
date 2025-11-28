using Syren.Server.Configuration;
using Syren.Server.Services;

namespace Syren.Server.Extensions;

public static class SnapCastExtensions
{
    public static IServiceCollection AddSnapCastServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<SnapCastOptions>(configuration.GetSection(SnapCastOptions.SectionName));

        services.AddSingleton<ISnapCastService, SnapCastService>();

        return services;
    }
}