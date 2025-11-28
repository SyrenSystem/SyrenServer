using System.Text.Json.Serialization;

namespace Syren.Server.Models.SnapCast;

public readonly struct Volume
{
    [JsonPropertyName("muted")]
    public required bool Muted { get; init; }

    [JsonPropertyName("percent")]
    public required int Percentage { get; init; }
}
