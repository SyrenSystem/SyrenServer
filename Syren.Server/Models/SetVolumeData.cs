using System.Text.Json.Serialization;

namespace Syren.Server.Models;

public readonly struct SetSpeakerVolumeData
{
    [JsonPropertyName("id")]
    public required string SensorId { get; init; }

    [JsonPropertyName("volume")]
    public required double Volume { get; init; }
}
