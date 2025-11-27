using System.Text.Json.Serialization;

namespace Syren.Server.Models.SnapCast;

public readonly struct Volume
{
    [JsonPropertyName("muted")]
    public required bool Muted { get; init; }

    [JsonPropertyName("percent")]
    public required int Percentage { get; init; }
}

public readonly struct SetVolumeRequest
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("jsonrpc")]
    public required string JsonRpcVersion { get; init; }

    [JsonPropertyName("method")]
    public required string Method { get; init; }

    [JsonPropertyName("params")]
    public required SetVolumeRequestParams Params { get; init; }
}

public readonly struct SetVolumeRequestParams
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }
    
    [JsonPropertyName("volume")]
    public required Volume Volume { get; init; }
}
