using System.Text.Json.Serialization;

namespace Syren.Server.Models;

public readonly struct DistanceData
{
    /// <summary>
    /// Sensor MAC address identifier (e.g., "1A:2B:3C:4D:5E")
    /// </summary>
    [JsonPropertyName("id")]
    public required string SpeakerId { get; init; }

    /// <summary>
    /// Distance in millimeters
    /// </summary>
    [JsonPropertyName("distance")]
    public required double Distance { get; init; }
}

public readonly struct DistancesData
{
    /// <summary>
    /// Array of distances
    /// </summary>
    [JsonPropertyName("distances")]
    public required DistanceData[] Distances { get; init; }
}
