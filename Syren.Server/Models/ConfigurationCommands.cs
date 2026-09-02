using System.Text.Json.Serialization;

namespace Syren.Server.Models;

public abstract class ConfigurationCommand
{
    [JsonPropertyName("requestId")]
    public required string RequestId { get; init; }

    [JsonPropertyName("expectedRevision")]
    public required long ExpectedRevision { get; init; }
}

public sealed class ConfigureSpeakerCommand : ConfigurationCommand
{
    [JsonPropertyName("speakerId")]
    public string? SpeakerId { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("snapClientId")]
    public required string SnapClientId { get; init; }

    [JsonPropertyName("sensorId")]
    public string? SensorId { get; init; }

    [JsonPropertyName("fullVolumeDistance")]
    public required double FullVolumeDistance { get; init; }

    [JsonPropertyName("muteDistance")]
    public required double MuteDistance { get; init; }
}

public sealed class DeleteSpeakerCommand : ConfigurationCommand
{
    [JsonPropertyName("speakerId")]
    public required string SpeakerId { get; init; }
}

public sealed class UpsertGroupCommand : ConfigurationCommand
{
    [JsonPropertyName("groupId")]
    public string? GroupId { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("speakerIds")]
    public required string[] SpeakerIds { get; init; }

    [JsonPropertyName("sourcePriority")]
    public required string[] SourcePriority { get; init; }

    [JsonPropertyName("volumeMode")]
    public required string VolumeMode { get; init; }

    [JsonPropertyName("masterVolume")]
    public required double MasterVolume { get; init; }

    [JsonPropertyName("muted")]
    public required bool Muted { get; init; }
}

public sealed class DeleteGroupCommand : ConfigurationCommand
{
    [JsonPropertyName("groupId")]
    public required string GroupId { get; init; }
}

public sealed class SetSpeakerLevelCommand : ConfigurationCommand
{
    [JsonPropertyName("speakerId")]
    public required string SpeakerId { get; init; }

    [JsonPropertyName("level")]
    public required double Level { get; init; }
}

public sealed class CommandResultMessage
{
    [JsonPropertyName("requestId")]
    public required string RequestId { get; init; }

    [JsonPropertyName("success")]
    public required bool Success { get; init; }

    [JsonPropertyName("revision")]
    public required long Revision { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}
