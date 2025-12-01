using System.Numerics;
using System.Text.Json.Serialization;

namespace Syren.Server.Models;

public readonly struct UserPosition
{
    /// <summary>
    /// Position in 3D space of the user
    /// </summary>
    [JsonPropertyName("position")]
    public required Vector3 Position { get; init; }
}
