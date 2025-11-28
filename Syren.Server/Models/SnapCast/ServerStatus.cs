using System.Text.Json.Serialization;

namespace Syren.Server.Models.SnapCast;

public readonly struct ServerStatus
{
    [JsonPropertyName("host")]
    public required Host Host { get; init; }

    [JsonPropertyName("snapserver")]
    public required SnapServerInfo SnapServerInfo { get; init; }
}
