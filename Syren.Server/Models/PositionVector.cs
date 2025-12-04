using System.Text.Json.Serialization;

namespace Syren.Server.Models;

public readonly struct PositionVector
{
    [JsonPropertyName("x")]
    public required double X { get; init; }

    [JsonPropertyName("y")]
    public required double Y { get; init; }

    [JsonPropertyName("z")]
    public required double Z { get; init; }
}
