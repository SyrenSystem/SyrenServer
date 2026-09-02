using System.Text.Json.Serialization;

namespace Syren.Server.Models;

public sealed class SystemConfigurationSnapshot
{
    [JsonPropertyName("stateId")]
    public required string StateId { get; init; }

    [JsonPropertyName("revision")]
    public required long Revision { get; init; }

    [JsonPropertyName("speakers")]
    public required SpeakerConfiguration[] Speakers { get; init; }

    [JsonPropertyName("groups")]
    public required PlaybackGroupConfiguration[] Groups { get; init; }

    [JsonPropertyName("sources")]
    public required AudioSourceConfiguration[] Sources { get; init; }
}

public sealed class SpeakerConfiguration
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

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

    [JsonPropertyName("level")]
    public required double Level { get; init; }

    [JsonPropertyName("calibrated")]
    public required bool Calibrated { get; init; }
}

public sealed class PlaybackGroupConfiguration
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

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

public sealed class AudioSourceConfiguration
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }
}

public sealed class SystemRuntimeSnapshot
{
    [JsonPropertyName("snapserverOnline")]
    public required bool SnapserverOnline { get; init; }

    [JsonPropertyName("onlineSnapClients")]
    public required SnapClientRuntime[] OnlineSnapClients { get; init; }

    [JsonPropertyName("sources")]
    public required AudioSourceRuntime[] Sources { get; init; }
}

public sealed record SnapClientRuntime
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }
}

public sealed record AudioSourceRuntime
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("active")]
    public required bool Active { get; init; }
}
