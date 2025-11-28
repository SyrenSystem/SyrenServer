using System.Text.Json.Serialization;

namespace Syren.Server.Models.SnapCast;

public readonly struct GetStatusResult {
    [JsonPropertyName("server")]
    public required SystemStatus SystemStatus { get; init; }
}

public readonly struct SystemStatus
{
    [JsonPropertyName("groups")]
    public required GroupStatus[] Groups { get; init; }

    [JsonPropertyName("server")]
    public required ServerStatus Server { get; init; }

    [JsonPropertyName("streams")]
    public required StreamStatus[] Streams { get; init; }

    public ClientStatus[] Clients() => Groups.SelectMany(group => group.Clients).ToArray();
}
