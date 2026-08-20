using System.Text.Json;
using Syren.Server.Models;
using Syren.Server.Models.SnapCast;

namespace Syren.Server.Services;

public sealed class SnapCastService : ISnapCastService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<SnapCastService> _logger;

    public SnapCastService(HttpClient httpClient, ILogger<SnapCastService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task SetClientVolumeAsync(
        string id,
        int percent,
        CancellationToken cancellationToken = default)
    {
        id = id.ToLowerInvariant();
        string requestId = Guid.NewGuid().ToString("N");
        using HttpResponseMessage response = await _httpClient.PostAsJsonAsync(
            "jsonrpc",
            new JsonRpcRequest<SetVolumeRequest>
            {
                RequestId = requestId,
                JsonRpcVersion = "2.0",
                Method = SetVolumeRequest.MethodName,
                Parameters = new SetVolumeRequest
                {
                    Id = id,
                    Volume = new Volume
                    {
                        Muted = false,
                        Percentage = percent,
                    },
                },
            },
            cancellationToken
        );
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        _logger.LogTrace("Snapserver response: {ResponseBody}", body);
        response.EnsureSuccessStatusCode();

        JsonRpcResponse<JsonElement> rpcResponse;
        try
        {
            rpcResponse = JsonSerializer.Deserialize<JsonRpcResponse<JsonElement>>(body);
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
            rpcResponse.Result.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            throw new InvalidOperationException("Snapserver returned an invalid JSON RPC result");
        }
    }
}
