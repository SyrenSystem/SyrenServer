using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Syren.Server.Interfaces;
using Syren.Server.Services;

var builder = Host.CreateDefaultBuilder();
builder.ConfigureServices(services =>
{
    services.AddLogging(options => options.AddConsole());
    services.AddTransient<IDistanceService, DistanceService>();
});

var app = builder.Build();

var distanceService = app.Services.GetRequiredService<IDistanceService>();

await app.RunAsync();
