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

public readonly struct StreamUriQuery
{
    [JsonPropertyName("chunk_ms")]
    public required string ChunkMilliSeconds { get; init; }

    [JsonPropertyName("codec")]
    public required string Codec { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("sampleformat")]
    public required string SampleFormat { get; init; }
}
