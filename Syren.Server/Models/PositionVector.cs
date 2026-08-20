using System.Text.Json.Serialization;
using System.Numerics;

namespace Syren.Server.Models;

public readonly struct PositionVector
{
    [JsonPropertyName("x")]
    public required double X { get; init; }

    [JsonPropertyName("y")]
    public required double Y { get; init; }

    [JsonPropertyName("z")]
    public required double Z { get; init; }

    public static PositionVector FromVector3(Vector3 position) => new()
    {
        X = position.X,
        Y = position.Y,
        Z = position.Z,
    };

    public Vector3 ToVector3() => new((float)X, (float)Y, (float)Z);
}
