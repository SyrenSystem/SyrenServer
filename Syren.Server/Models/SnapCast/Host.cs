using System.Text.Json.Serialization;

namespace Syren.Server.Models.SnapCast;

public readonly struct Host
{
    [JsonPropertyName("arch")]
    public required string Architecture { get; init; }

    [JsonPropertyName("ip")]
    public required string IpAddress { get; init; }

    [JsonPropertyName("mac")]
    public required string MacAddress { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("os")]
    public required string OperatingSystem { get; init; }
}
