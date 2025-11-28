using System.Numerics;
using Syren.Server.Models;

namespace Syren.Server.Extensions;

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

