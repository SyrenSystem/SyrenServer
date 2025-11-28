using System.Text.Json.Serialization;

namespace Syren.Server.Models.SnapCast;

public readonly struct StreamStatus
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("uri")]
    public required StreamUri Uri { get; init; }
}
