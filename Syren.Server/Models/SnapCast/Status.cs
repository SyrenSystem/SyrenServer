using System.Text.Json.Serialization;

namespace Syren.Server.Models.SnapCast;

public readonly struct GetStatusRequest
{
    [JsonPropertyName("id")]
    public required int Id { get; init; }

    [JsonPropertyName("jsonrpc")]
    public required string JsonRpcVersion { get; init; }

    [JsonPropertyName("method")]
    public required string Method { get; init; }
}

public readonly struct Status
{
    [JsonPropertyName("id")]
    public required int Id { get; init; }

    [JsonPropertyName("jsonrpc")]
    public required string JsonRpcVersion { get; init; }

    [JsonPropertyName("result")]
    public required SystemStatusResult Result { get; init; }
}

public readonly struct SystemStatusResult {
    [JsonPropertyName("server")]
    public required SystemStatus SystemStatus { get; init; }
}

public readonly struct SystemStatus
{
    [JsonPropertyName("groups")]
    public required GroupStatus[] Groups { get; init; }

    [JsonPropertyName("server")]
    public required ServerStatus Server { get; init; }

    [JsonPropertyName("streams")]
    public required StreamStatus[] Streams { get; init; }
}
