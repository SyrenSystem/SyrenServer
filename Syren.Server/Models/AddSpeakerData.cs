using System.Text.Json.Serialization;

namespace Syren.Server.Models;

public readonly struct AddSpeakerData
{
    /// <summary>
    /// Sensor MAC address identifier (e.g., "1A:2B:3C:4D:5E")
    /// <summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }
}