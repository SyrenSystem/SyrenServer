using System.Text.Json.Serialization;

namespace Syren.Server.Models.SnapCast;

public readonly struct LastSeenStatus
{
    [JsonPropertyName("sec")]
    public required int Seconds { get; init; }

    [JsonPropertyName("usec")]
    public required int USeconds { get; init; }
}
