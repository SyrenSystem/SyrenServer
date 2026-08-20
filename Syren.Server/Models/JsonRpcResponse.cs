using System.Text.Json.Serialization;
using System.Text.Json;

namespace Syren.Server.Models;

public readonly struct JsonRpcResponse<T>
{
    [JsonPropertyName("jsonrpc")]
    public string? JsonRpcVersion { get; init; }

    [JsonPropertyName("id")]
    public string? RequestId { get; init; }

    [JsonPropertyName("result")]
    public T? Result { get; init; }

    [JsonPropertyName("error")]
    public JsonRpcError? Error { get; init; }
}

public sealed class JsonRpcError
{
    [JsonPropertyName("code")]
    public int Code { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("data")]
    public JsonElement? Data { get; init; }
}
