using System.Text.Json.Serialization;

namespace Syren.Server.Models.SnapCast;

public readonly struct StreamUri
{
    [JsonPropertyName("fragment")]
    public required string Fragment { get; init; }

    [JsonPropertyName("host")]
    public required string Host { get; init; }

    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("query")]
    public required StreamUriQuery Query { get; init; }

    [JsonPropertyName("raw")]
    public required string Raw { get; init; }

    [JsonPropertyName("scheme")]
    public required string Scheme { get; init; }
}
