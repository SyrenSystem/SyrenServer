using System.Numerics;
using Syren.Server.Models;

namespace Syren.Server.Services;

public interface IDistanceService
{
    public void UpdateDistances(IReadOnlyCollection<DistanceData> distances);
    public Speaker AddSpeaker(string id);
    public void RemoveSpeaker(string id);

    public Vector3 GetUserPosition(IReadOnlyCollection<DistanceData> distances);
}
