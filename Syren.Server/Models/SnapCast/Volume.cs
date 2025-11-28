using System.Text.Json.Serialization;

namespace Syren.Server.Models.SnapCast;

public readonly struct Volume
{
    [JsonPropertyName("muted")]
    public required bool Muted { get; init; }

    [JsonPropertyName("percent")]
    public required int Percentage { get; init; }
}

public readonly struct SetVolumeRequest
{
    public const string MethodName = "Client.SetVolume";

    [JsonPropertyName("id")]
    public required string Id { get; init; }
    
    [JsonPropertyName("volume")]
    public required Volume Volume { get; init; }
}
