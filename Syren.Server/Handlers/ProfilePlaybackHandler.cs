using System.Text.Json;
using MQTTnet;
using Syren.Server.Services;

namespace Syren.Server.Handlers;

public sealed class ProfilePlaybackHandler(
    ProfilePlaybackCoordinator coordinator,
    string topic,
    ILogger<ProfilePlaybackHandler>? logger = null) : IMqttMessageHandler
{
    public const string Prefix = "SyrenSystem/v3/";
    private readonly object _commandSync = new();
    private Task _commands = Task.CompletedTask;

    public string Topic { get; } = Prefix + topic;

    // Completes when every queued command has published its result.
    public Task CommandsCompleted
    {
        get
        {
            lock (_commandSync)
            {
                return _commands;
            }
        }
    }

    public async Task HandleMessageAsync(MqttApplicationMessage message, IMqttClientService client,
        CancellationToken cancellationToken = default)
    {
        using JsonDocument document = JsonDocument.Parse(message.Payload);
        JsonElement payload = document.RootElement;
        if (topic == "Command")
        {
            string requestId = payload.GetProperty("requestId").GetString() ?? "";
            if (requestId.Length is 0 or > 128 || requestId.Any(character => character is '/' or '+' or '#'))
            {
                throw new JsonException("Invalid request identity");
            }
            // MQTTnet handles one message at a time, so slow Snapcast calls must not hold up heartbeats.
            JsonElement command = payload.Clone();
            lock (_commandSync)
            {
                _commands = _commands.ContinueWith(_ => RunCommandAsync(command, requestId, client),
                    CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
            }
        }
        else if (topic == "Lifecycle")
        {
            if (coordinator.Lifecycle(payload) == LifecycleOutcome.UnknownSession)
            {
                // Tells the producer to stop resending events the server will never accept.
                await client.PublishAsync(Prefix + "LifecycleRejected", new
                {
                    generation = coordinator.Generation,
                    sessionId = payload.GetProperty("sessionId").GetString(),
                    producerId = payload.GetProperty("producerId").GetString(),
                }, cancellationToken: cancellationToken);
            }
        }
        else if (topic == "Position")
        {
            coordinator.Position(payload);
        }
        else if (topic == "SpotifyLinked")
        {
            bool success = coordinator.CompleteSpotifyLink(payload, out string? error);
            await client.PublishAsync(Prefix + "SpotifyLinkStatus", new
            {
                ticket = payload.GetProperty("ticket").GetString(), status = success ? "linked" : "rejected", error,
            }, cancellationToken: cancellationToken);
        }
        else if (topic == "ReceiverStatus")
        {
            coordinator.ReceiverStatus(payload);
        }
    }

    private async Task RunCommandAsync(JsonElement payload, string requestId, IMqttClientService client)
    {
        try
        {
            object result;
            try
            {
                result = await coordinator.CommandAsync(payload, CancellationToken.None);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                result = new { requestId, success = false, error = CommandError(exception) };
            }
            JsonElement response = JsonSerializer.SerializeToElement(result);
            if (response.TryGetProperty("linkRequest", out JsonElement linkRequest))
            {
                await client.PublishAsync(Prefix + "SpotifyLinkRequest", linkRequest);
            }
            await client.PublishAsync(Prefix + "Result/" + requestId, result);
        }
        catch (Exception exception)
        {
            logger?.LogWarning(exception, "Unable to finish profile command {RequestId}", requestId);
        }
    }

    private string CommandError(Exception exception)
    {
        switch (exception)
        {
            case InvalidOperationException or InvalidDataException or ArgumentException:
                return exception.Message;
            case JsonException or KeyNotFoundException or FormatException:
                return "The command is missing a field or has an invalid value";
            case SnapServerUnavailableException or HttpRequestException or IOException or TimeoutException:
                return "The audio server is unavailable; try again";
            default:
                logger?.LogError(exception, "Profile command failed");
                return "The server could not complete the command";
        }
    }
}
