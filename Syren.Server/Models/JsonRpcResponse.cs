using System.Text.Json.Serialization;

namespace Syren.Server.Models;

public readonly struct JsonRpcResponse<T>
{
    [JsonPropertyName("jsonrpc")]
    public required string JsonRpcVersion { get; init; }

    [JsonPropertyName("id")]
    public required string RequestId { get; init; }

    [JsonPropertyName("result")]
    public required T Result { get; init; }
}
