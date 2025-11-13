using System.Numerics;
using Syren.Server.Models;

namespace Syren.Server.Interfaces;

public interface IDistanceService
{
    public void UpdateDistances(IReadOnlyCollection<DistanceData> distances);
    public Speaker AddSpeaker();
    public void RemoveSpeaker(uint id);

    public Vector3 GetUserPosition(IReadOnlyCollection<DistanceData> distances);
}
