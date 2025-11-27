using System.Text.Json.Serialization;

namespace Syren.Server.Models.SnapCast;

public readonly struct ClientStatus
{
    [JsonPropertyName("config")]
    public required ClientConfig Config { get; init; }

    [JsonPropertyName("connected")]
    public required bool Connected { get; init; }

    [JsonPropertyName("host")]
    public required Host Host { get; init; }

    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("lastSeen")]
    public required LastSeenStatus LastSeen { get; init; }

    [JsonPropertyName("snapclient")]
    public required SnapClientInfo SnapClient { get; init; }
}

public readonly struct ClientConfig
{
    [JsonPropertyName("instance")]
    public required int Instance { get; init; }
    
    [JsonPropertyName("latency")]
    public required int Latency { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("volume")]
    public required Volume Volume { get; init; }
}

public readonly struct SnapClientInfo
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("protocolVersion")]
    public required int ProtocolVersion { get; init; }

    [JsonPropertyName("version")]
    public required string Version { get; init; }
}

public readonly struct LastSeenStatus
{
    [JsonPropertyName("sec")]
    public required int Seconds { get; init; }

    [JsonPropertyName("usec")]
    public required int USeconds { get; init; }
}
