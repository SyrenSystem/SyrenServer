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
