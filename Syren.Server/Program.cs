using Syren.Server.Extensions;
using Syren.Server.Services;

var builder = WebApplication.CreateBuilder();

builder.Services.AddLogging(options => options.AddConsole());
builder.Services.AddSingleton<IDistanceService, DistanceService>();
builder.Services.AddMqttServices(builder.Configuration);

var app = builder.Build();

var distanceService = app.Services.GetRequiredService<IDistanceService>();

app.Run();
