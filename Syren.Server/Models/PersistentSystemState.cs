using System.Text.Json.Serialization;

namespace Syren.Server.Models;

public sealed record PersistentSystemState
{
    [JsonPropertyName("version")]
    public int Version { get; init; } = 2;

    [JsonPropertyName("stateId")]
    public required string StateId { get; init; }

    [JsonPropertyName("revision")]
    public long Revision { get; init; }

    [JsonPropertyName("speakers")]
    public List<PersistentSpeakerState> Speakers { get; init; } = [];

    [JsonPropertyName("groups")]
    public List<PersistentPlaybackGroup> Groups { get; init; } = [];

    [JsonPropertyName("retiredSensorIds")]
    public List<string> RetiredSensorIds { get; init; } = [];
}

public sealed record PersistentSpeakerState
{
    [JsonPropertyName("speakerId")]
    public string? SpeakerId { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("sensorId")]
    public string? SensorId { get; init; }

    [JsonPropertyName("snapClientId")]
    public string? SnapClientId { get; init; }

    [JsonPropertyName("fullVolumeDistance")]
    public double FullVolumeDistance { get; init; } = 1000;

    [JsonPropertyName("muteDistance")]
    public double MuteDistance { get; init; } = 5000;

    [JsonPropertyName("connected")]
    public required bool Connected { get; init; }

    [JsonPropertyName("volume")]
    public required double Volume { get; init; }

    [JsonPropertyName("position")]
    public PositionVector? Position { get; init; }
}

public sealed record PersistentPlaybackGroup
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("speakerIds")]
    public List<string> SpeakerIds { get; init; } = [];

    [JsonPropertyName("sourcePriority")]
    public List<string> SourcePriority { get; init; } = [];

    [JsonPropertyName("volumeMode")]
    public string VolumeMode { get; init; } = "manual";

    [JsonPropertyName("masterVolume")]
    public double MasterVolume { get; init; } = 100;

    [JsonPropertyName("muted")]
    public bool Muted { get; init; }
}
