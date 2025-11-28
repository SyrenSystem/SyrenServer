using System.Text.Json.Serialization;

namespace Syren.Server.Models.SnapCast;

public readonly struct SetVolumeRequest
{
    public const string MethodName = "Client.SetVolume";

    [JsonPropertyName("id")]
    public required string Id { get; init; }
    
    [JsonPropertyName("volume")]
    public required Volume Volume { get; init; }
}
