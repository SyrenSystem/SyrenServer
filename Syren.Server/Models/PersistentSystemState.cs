using System.Text.Json.Serialization;

namespace Syren.Server.Models;

public sealed class PersistentSystemState
{
    [JsonPropertyName("version")]
    public int Version { get; init; } = 1;

    [JsonPropertyName("stateId")]
    public required string StateId { get; init; }

    [JsonPropertyName("speakers")]
    public List<PersistentSpeakerState> Speakers { get; init; } = [];

    [JsonPropertyName("retiredSensorIds")]
    public List<string> RetiredSensorIds { get; init; } = [];
}

public sealed class PersistentSpeakerState
{
    [JsonPropertyName("sensorId")]
    public required string SensorId { get; init; }

    [JsonPropertyName("connected")]
    public required bool Connected { get; init; }

    [JsonPropertyName("volume")]
    public required double Volume { get; init; }

    [JsonPropertyName("position")]
    public PositionVector? Position { get; init; }
}
