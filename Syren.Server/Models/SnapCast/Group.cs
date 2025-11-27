using System.Text.Json.Serialization;

namespace Syren.Server.Models.SnapCast;

public readonly struct GroupStatus
{
    [JsonPropertyName("clients")]
    public required ClientStatus[] Clients { get; init; }

    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("muted")]
    public required bool Muted { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("stream_id")]
    public required string StreamId { get; init; }
}
