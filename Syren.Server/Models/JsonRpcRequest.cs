using System.Text.Json.Serialization;

namespace Syren.Server.Models;

public readonly struct JsonRpcRequest<T>
{
    [JsonPropertyName("jsonrpc")]
    public required string JsonRpcVersion { get; init; }

    [JsonPropertyName("id")]
    public required string RequestId { get; init; }

    [JsonPropertyName("method")]
    public required string Method { get; init; }

    [JsonPropertyName("params")]
    public required T Parameters { get; init; }
}

public readonly struct JsonRpcRequest
{
    [JsonPropertyName("jsonrpc")]
    public required string JsonRpcVersion { get; init; }

    [JsonPropertyName("id")]
    public required string RequestId { get; init; }

    [JsonPropertyName("method")]
    public required string Method { get; init; }
}
