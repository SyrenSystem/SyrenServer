using System.Text.Json.Serialization;

namespace Syren.Server.Models;

public readonly struct SetSpeakerVolumeData
{
    [JsonPropertyName("id")]
    public required string sensorId { get; init; }

    [JsonPropertyName("volume")]
    public required double volume { get; init; }
}
