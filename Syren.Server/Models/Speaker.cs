using System.Numerics;

namespace Syren.Server.Models;

public struct Speaker
{
    public required string SensorId;
    public required string SnapClientId;

    public required double FullVolumeDistance;
    public required double MuteDistance;
}

public sealed record SpeakerState
{
    public Speaker Speaker;

    public double Distance;
    public Vector3 Position;

    public double Volume;
}

public static class SpeakerStateExtensions
{
    public static Vector3 Center(this IEnumerable<SpeakerState> speakers)
    {
        int count = 0;
        Vector3 center = Vector3.Zero;

        foreach (SpeakerState speaker in speakers)
        {
            count++;
            center += speaker.Position;
        }

        return count == 0 ? center : center / count;
    }
}

