using System.Text.Json.Serialization;

namespace Syren.Server.Models.SnapCast;

public readonly struct ClientConfig
{
    [JsonPropertyName("instance")]
    public required int Instance { get; init; }
    
    [JsonPropertyName("latency")]
    public required int Latency { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("volume")]
    public required Volume Volume { get; init; }
}
