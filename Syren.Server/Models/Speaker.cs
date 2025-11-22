using System.Numerics;

namespace Syren.Server.Models;

public struct Speaker
{
    public required string Id;
    public required Vector3 Position;
}

public static class SpeakerExtensions
{
    public static Vector3 Center(this IEnumerable<Speaker> speakers)
    {
        int count = 0;
        Vector3 center = Vector3.Zero;

        foreach (Speaker speaker in speakers)
        {
            count++;
            center += speaker.Position;
        }

        return count == 0 ? center : center / count;
    }
}
