using System.Text.Json.Serialization;

namespace Syren.Server.Models.SnapCast;

public sealed class SnapServerStatusResult
{
    [JsonPropertyName("server")]
    public required SnapServerStatus Server { get; init; }
}

public sealed class SnapServerStatus
{
    [JsonPropertyName("groups")]
    public SnapGroupStatus[] Groups { get; init; } = [];

    [JsonPropertyName("streams")]
    public SnapStreamStatus[] Streams { get; init; } = [];
}

public sealed class SnapGroupStatus
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("stream_id")]
    public string StreamId { get; init; } = string.Empty;

    [JsonPropertyName("muted")]
    public bool Muted { get; init; }

    [JsonPropertyName("clients")]
    public SnapClientStatus[] Clients { get; init; } = [];
}

public sealed class SnapClientStatus
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("connected")]
    public bool Connected { get; init; }

    [JsonPropertyName("host")]
    public SnapHostStatus? Host { get; init; }

    [JsonPropertyName("config")]
    public SnapClientConfigStatus? Config { get; init; }
}

public sealed class SnapClientConfigStatus
{
    [JsonPropertyName("volume")]
    public SnapClientVolumeStatus? Volume { get; init; }
}

public sealed class SnapClientVolumeStatus
{
    [JsonPropertyName("percent")]
    public int Percent { get; init; }

    [JsonPropertyName("muted")]
    public bool Muted { get; init; }
}

public sealed class SnapHostStatus
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;
}

public sealed class SnapStreamStatus
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("status")]
    public string Status { get; init; } = "idle";
}
