using System.Text.Json.Serialization;

namespace Syren.Server.Models.SnapCast;

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
