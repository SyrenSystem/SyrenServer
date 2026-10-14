using System.Text.Json.Serialization;

namespace Syren.Server.Models;

public sealed record PositionReporterOwner
{
    [JsonPropertyName("profileId")]
    public required string ProfileId { get; init; }
    [JsonPropertyName("instanceId")]
    public required string InstanceId { get; init; }
    [JsonPropertyName("lease")]
    public required string Lease { get; init; }
}

public sealed record ListenerProfile
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }
    [JsonPropertyName("name")]
    public required string Name { get; init; }
    [JsonPropertyName("followMe")]
    public bool FollowMe { get; init; }
    [JsonPropertyName("sourcePriority")]
    public List<string> SourcePriority { get; init; } = ["spotify", "laptop", "casting"];
    [JsonPropertyName("overlap")]
    public List<SourceOverlap> Overlap { get; init; } = [];
    [JsonPropertyName("spotifyAccountId")]
    public string? SpotifyAccountId { get; init; }
    [JsonPropertyName("castingDevices")]
    public List<LinkedCastingDevice> CastingDevices { get; init; } = [];
}

public sealed record LinkedCastingDevice
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }
    [JsonPropertyName("name")]
    public required string Name { get; init; }
}

public sealed record SourceOverlap
{
    [JsonPropertyName("first")]
    public required string First { get; init; }
    [JsonPropertyName("second")]
    public required string Second { get; init; }
}

public sealed record SessionTransport
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }
    [JsonPropertyName("endpoint")]
    public required string Endpoint { get; init; }
    [JsonPropertyName("tcpPort")]
    public int? TcpPort { get; init; }
    [JsonPropertyName("speakerId")]
    public string? SpeakerId { get; init; }
    [JsonPropertyName("available")]
    public bool Available { get; init; }
    [JsonPropertyName("latencyMsec")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? LatencyMsec { get; init; }
}

public sealed record PlaybackSession
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }
    [JsonPropertyName("producerId")]
    public required string ProducerId { get; init; }
    [JsonPropertyName("ownerId")]
    public required string OwnerId { get; init; }
    [JsonPropertyName("accountId")]
    public string? AccountId { get; init; }
    [JsonPropertyName("source")]
    public required string Source { get; init; }
    [JsonPropertyName("destination")]
    public required string Destination { get; init; }
    [JsonPropertyName("state")]
    public string State { get; init; } = "connected";
    [JsonPropertyName("claimSequence")]
    public long ClaimSequence { get; init; }
    [JsonPropertyName("claimed")]
    public bool Claimed { get; init; }
    [JsonPropertyName("eventSequence")]
    public long EventSequence { get; init; }
    [JsonPropertyName("transports")]
    public List<SessionTransport> Transports { get; init; } = [];
    [JsonPropertyName("pcMode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PcMode { get; init; }
    [JsonPropertyName("interruptedAt")]
    public DateTimeOffset? InterruptedAt { get; init; }
    [JsonPropertyName("protocol")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Protocol { get; init; }
    // A keyed hash of the sender, so receivers never mix two sessions from one device.
    [JsonPropertyName("senderId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SenderId { get; init; }
    // The server decides this so receivers never compare clocks.
    [JsonPropertyName("eligible")]
    public bool Eligible { get; init; }
    // When the current pause began, saved so a server restart does not restart the paused period.
    [JsonPropertyName("pausedAt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? PausedAt { get; init; }

    public PlaybackSession End() => this with
    {
        State = "ended", Claimed = false, Eligible = false, Transports = [], InterruptedAt = null, PausedAt = null,
    };
}

public sealed record SessionCatalogue
{
    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion => 3;
    [JsonPropertyName("stateId")]
    public required string StateId { get; init; }
    [JsonPropertyName("generation")]
    public required long Generation { get; init; }
    [JsonPropertyName("revision")]
    public required long Revision { get; init; }
    [JsonPropertyName("configurationRevision")]
    public required long ConfigurationRevision { get; init; }
    [JsonPropertyName("sessions")]
    public required PlaybackSession[] Sessions { get; init; }
    [JsonPropertyName("profiles")]
    public required ListenerProfile[] Profiles { get; init; }
    [JsonPropertyName("sourcePolicies")]
    public required Dictionary<string, string> SourcePolicies { get; init; }
}

public sealed record SessionLifecycleEvent
{
    [JsonPropertyName("generation")]
    public long Generation { get; init; }
    [JsonPropertyName("sessionId")]
    public required string SessionId { get; init; }
    [JsonPropertyName("producerId")]
    public required string ProducerId { get; init; }
    [JsonPropertyName("eventSequence")]
    public long EventSequence { get; init; }
    [JsonPropertyName("action")]
    public required string Action { get; init; }
    [JsonPropertyName("ownerId")]
    public string? OwnerId { get; init; }
    [JsonPropertyName("accountId")]
    public string? AccountId { get; init; }
    [JsonPropertyName("source")]
    public string? Source { get; init; }
    [JsonPropertyName("destination")]
    public string? Destination { get; init; }
    [JsonPropertyName("transports")]
    public List<SessionTransport>? Transports { get; init; }
    [JsonPropertyName("pcMode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PcMode { get; init; }
    [JsonPropertyName("protocol")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Protocol { get; init; }
    [JsonPropertyName("accountName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AccountName { get; init; }
}

public sealed class ProfileConfigurationCommand : ConfigurationCommand
{
    [JsonPropertyName("protocolVersion")]
    public int ProtocolVersion { get; init; }
    [JsonPropertyName("stateId")]
    public required string StateId { get; init; }
    [JsonPropertyName("generation")]
    public long Generation { get; init; }
    [JsonPropertyName("action")]
    public required string Action { get; init; }
    [JsonPropertyName("profile")]
    public ListenerProfile? Profile { get; init; }
    [JsonPropertyName("profileId")]
    public string? ProfileId { get; init; }
    [JsonPropertyName("source")]
    public string? Source { get; init; }
    [JsonPropertyName("policy")]
    public string? Policy { get; init; }
    [JsonPropertyName("deviceId")]
    public string? DeviceId { get; init; }
}
