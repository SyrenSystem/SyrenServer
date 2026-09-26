using System.Text.Json;
using System.Text.Json.Serialization;
using Syren.Server.Models;
using Syren.Server.Models.SnapCast;

namespace Syren.Server.Services;

public sealed class SnapCastService : ISnapCastService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _httpClient;
    private readonly ILogger<SnapCastService> _logger;

    public SnapCastService(HttpClient httpClient, ILogger<SnapCastService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public Task SetClientVolumeAsync(
        string id,
        int percent,
        CancellationToken cancellationToken = default) => CallWithoutResultAsync(
        SetVolumeRequest.MethodName,
        new SetVolumeRequest
        {
            Id = Identifiers.Normalize(id),
            Volume = new Volume { Muted = false, Percentage = percent },
        },
        cancellationToken
    );

    public async Task<SnapServerStatus> GetStatusAsync(
        CancellationToken cancellationToken = default)
    {
        SnapServerStatusResult result = await CallAsync<SnapServerStatusResult>(
            "Server.GetStatus",
            null,
            cancellationToken
        );
        return result.Server;
    }

    public Task SetGroupClientsAsync(
        string id,
        IReadOnlyList<string> clientIds,
        CancellationToken cancellationToken = default) => CallWithoutResultAsync(
        "Group.SetClients",
        new { id, clients = clientIds.Select(Identifiers.Normalize).ToArray() },
        cancellationToken
    );

    public Task SetGroupNameAsync(
        string id,
        string name,
        CancellationToken cancellationToken = default) => CallWithoutResultAsync(
        "Group.SetName",
        new { id, name },
        cancellationToken
    );

    public Task SetGroupStreamAsync(
        string id,
        string streamId,
        CancellationToken cancellationToken = default) => CallWithoutResultAsync(
        "Group.SetStream",
        new { id, stream_id = streamId },
        cancellationToken
    );

    public Task SetGroupMuteAsync(
        string id,
        bool muted,
        CancellationToken cancellationToken = default) => CallWithoutResultAsync(
        "Group.SetMute",
        new { id, mute = muted },
        cancellationToken
    );

    public async Task<string> AddStreamAsync(
        string streamUri,
        CancellationToken cancellationToken = default)
    {
        StreamMutationResult result = await CallAsync<StreamMutationResult>(
            "Stream.AddStream",
            new { streamUri },
            cancellationToken
        );
        return result.StreamId ?? result.Id ?? throw new InvalidOperationException(
            "Snapserver did not return the added stream ID"
        );
    }

    public Task RemoveStreamAsync(
        string streamId,
        CancellationToken cancellationToken = default) => CallWithoutResultAsync(
        "Stream.RemoveStream",
        new { id = streamId },
        cancellationToken
    );

    public Task DeleteClientAsync(
        string id,
        CancellationToken cancellationToken = default) => CallWithoutResultAsync(
        "Server.DeleteClient",
        new { id = Identifiers.Normalize(id) },
        cancellationToken
    );

    private async Task CallWithoutResultAsync(
        string method,
        object? parameters,
        CancellationToken cancellationToken)
    {
        await CallAsync<JsonElement>(method, parameters, cancellationToken);
    }

    private async Task<TResult> CallAsync<TResult>(
        string method,
        object? parameters,
        CancellationToken cancellationToken)
    {
        string requestId = Guid.NewGuid().ToString("N");
        string body;
        try
        {
            using HttpResponseMessage response = await _httpClient.PostAsJsonAsync(
                "jsonrpc",
                new JsonRpcRequest<object?>
                {
                    RequestId = requestId,
                    JsonRpcVersion = "2.0",
                    Method = method,
                    Parameters = parameters,
                },
                SerializerOptions,
                cancellationToken
            );
            body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogTrace("Snapserver response: {ResponseBody}", body);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException exception)
        {
            throw new SnapServerUnavailableException(
                $"Snapserver request {method} failed: {exception.Message}",
                exception
            );
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SnapServerUnavailableException(
                $"Snapserver request {method} timed out",
                exception
            );
        }

        JsonRpcResponse<TResult> rpcResponse;
        try
        {
            rpcResponse = JsonSerializer.Deserialize<JsonRpcResponse<TResult>>(
                body,
                SerializerOptions
            );
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Snapserver returned malformed JSON", exception);
        }

        if (rpcResponse.Error != null)
        {
            _logger.LogError(
                "Snapserver RPC error {Code}: {Message}",
                rpcResponse.Error.Code,
                rpcResponse.Error.Message
            );
            throw new InvalidOperationException(
                $"Snapserver RPC error {rpcResponse.Error.Code}: {rpcResponse.Error.Message}"
            );
        }

        if (rpcResponse.JsonRpcVersion != "2.0" ||
            rpcResponse.RequestId != requestId ||
            IsMissing(rpcResponse.Result))
        {
            throw new InvalidOperationException("Snapserver returned an invalid JSON RPC result");
        }
        return rpcResponse.Result!;
    }

    private static bool IsMissing<TResult>(TResult? result) => result switch
    {
        null => true,
        JsonElement element => element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null,
        _ => false,
    };

    private sealed class StreamMutationResult
    {
        [JsonPropertyName("stream_id")]
        public string? StreamId { get; init; }

        [JsonPropertyName("id")]
        public string? Id { get; init; }
    }
}
