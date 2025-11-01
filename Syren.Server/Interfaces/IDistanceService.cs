using System.Numerics;
using Syren.Server.Models;

namespace Syren.Server.Interfaces;

public interface IDistanceService
{
    public Vector3 GetUserPosition(IReadOnlyCollection<DistanceData> distances);
}
