using System.Text.Json.Serialization;

namespace Syren.Server.Models.SnapCast;

public readonly struct ServerStatus
{
    [JsonPropertyName("host")]
    public required Host Host { get; init; }

    [JsonPropertyName("snapserver")]
    public required SnapServerInfo SnapServerInfo { get; init; }
}

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
