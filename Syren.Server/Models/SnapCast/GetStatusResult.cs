using System.Text.Json.Serialization;

namespace Syren.Server.Models.SnapCast;

public readonly struct GetStatusResult {
    [JsonPropertyName("server")]
    public required SystemStatus SystemStatus { get; init; }
}
