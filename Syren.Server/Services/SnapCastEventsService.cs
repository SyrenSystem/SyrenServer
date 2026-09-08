using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Syren.Server.Configuration;

namespace Syren.Server.Services;

public sealed class SnapCastEventsService(
    IOptions<SnapCastOptions> options,
    SystemConfigurationService configuration,
    ILogger<SnapCastEventsService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var endpoint = new UriBuilder("ws", options.Value.ServerHost, options.Value.HttpPort, "/jsonrpc").Uri;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var connection = new ClientWebSocket();
                connection.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
                connection.Options.KeepAliveTimeout = TimeSpan.FromSeconds(5);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(options.Value.RequestTimeoutSeconds));
                await connection.ConnectAsync(endpoint, timeout.Token);
                configuration.SignalReconcile();
                await ObserveAsync(connection, configuration.SignalReconcile, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is WebSocketException or IOException or OperationCanceledException or JsonException)
            {
                logger.LogDebug(exception, "Snapserver event connection interrupted; polling remains available");
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    internal static async Task ObserveAsync(WebSocket connection, Action sourceChanged, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[8192];
        using var message = new MemoryStream();
        while (!cancellationToken.IsCancellationRequested)
        {
            var result = await connection.ReceiveAsync(buffer.AsMemory(), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close) return;
            if (result.MessageType != WebSocketMessageType.Text || message.Length + result.Count > 1024 * 1024)
                throw new IOException("Invalid Snapserver event message");
            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;
            using var document = JsonDocument.Parse(message.GetBuffer().AsMemory(0, (int)message.Length));
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("method", out var method) &&
                method.ValueKind == JsonValueKind.String &&
                method.GetString() == "Stream.OnUpdate")
                sourceChanged();
            message.SetLength(0);
        }
    }
}
