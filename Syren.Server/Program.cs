using Syren.Server.Hubs;
using Syren.Server.Interfaces;
using Syren.Server.Services;

var builder = WebApplication.CreateBuilder();

builder.Services.AddLogging(options => options.AddConsole());
builder.Services.AddSingleton<IDistanceService, DistanceService>();
builder.Services.AddSignalR();

var app = builder.Build();

var distanceService = app.Services.GetRequiredService<IDistanceService>();

app.MapHub<DistanceHub>("/distance");

app.Run();
