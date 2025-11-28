using System.Numerics;

namespace Syren.Server.Models;

public sealed record SpeakerState
{
    public Speaker Speaker;

    public double Distance;
    public Vector3 Position;

    public double Volume;
}
