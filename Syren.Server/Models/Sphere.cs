using System.Numerics;

namespace Syren.Server.Models;

public readonly struct Sphere
{
    public required Vector3 Center { get; init; }
    public required double Radius { get; init; }
}
