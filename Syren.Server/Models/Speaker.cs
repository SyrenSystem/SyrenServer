using System.Numerics;

namespace Syren.Server.Models;

public struct Speaker
{
    public required uint Id;
    public required Vector3 Position;
}

public static class SpeakerExtensions
{
    public static Vector3 Center(this IEnumerable<Speaker> speakers)
    {
        uint count = 0;
        Vector3 center = Vector3.Zero;

        foreach (Speaker speaker in speakers)
        {
            count++;
            center += speaker.Position;
        }

        return count == 0 ? center : center / count;
    }
}
