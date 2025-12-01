using System.Text.Json.Serialization;

namespace Syren.Server.Models;

public readonly struct UserPosition
{
    /// <summary>
    /// Position in 3D space of the user
    /// </summary>
    [JsonPropertyName("position")]
    public required PositionVector Position { get; init; }
}
