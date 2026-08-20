using Syren.Server.Extensions;
using Syren.Server.Services;

var builder = WebApplication.CreateBuilder();

builder.Services.AddLogging(options => options.AddConsole());
builder.Services.AddSnapCastServices(builder.Configuration);
builder.Services.AddDistanceServices(builder.Configuration);
builder.Services.AddMqttServices(builder.Configuration);

var app = builder.Build();

app.Run();
