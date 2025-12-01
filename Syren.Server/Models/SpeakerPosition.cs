using System.Text.Json.Serialization;

namespace Syren.Server.Models;

public readonly struct SpeakerPosition
{
    /// <summary>
    /// Sensor MAC address identifier (e.g. "1A:2B:3C:4D:5E")
    /// </summary>
    [JsonPropertyName("id")]
    public required string SpeakerId { get; init; }

    /// <summary>
    /// Position in 3D space of the speaker
    /// </summary>
    [JsonPropertyName("position")]
    public required PositionVector Position { get; init; }
}
