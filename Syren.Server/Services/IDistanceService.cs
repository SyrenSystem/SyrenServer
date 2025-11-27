using System.Numerics;
using Syren.Server.Models;

namespace Syren.Server.Services;

public interface IDistanceService
{
    public void UpdateDistances(IReadOnlyCollection<DistanceData> distances);
    public Task<SpeakerState?> ConnectSpeakerAsync(string sensorId);
    public void DisconnectSpeaker(string sensorId);

    public Vector3 GetUserPosition(IReadOnlyCollection<DistanceData> distances);
}
