using System.Text.Json.Serialization;

namespace Syren.Server.Models.SnapCast;

public readonly struct SnapServerInfo
{
    [JsonPropertyName("controlProtocolVersion")]
    public required int ControlProtocolVersion { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    
    [JsonPropertyName("protocolVersion")]
    public required int ProtocolVersion { get; init; }

    [JsonPropertyName("version")]
    public required string Version { get; init; }
}
