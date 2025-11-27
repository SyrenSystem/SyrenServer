using System.Numerics;
using Syren.Server.Models;

namespace Syren.Server.Services;

public interface IDistanceService
{
    public Task UpdateDistances(IReadOnlyCollection<DistanceData> distances);
    public Task<SpeakerState?> ConnectSpeakerAsync(string sensorId);
    public Task DisconnectSpeaker(string sensorId);

    public Vector3 GetUserPosition(IReadOnlyCollection<DistanceData> distances);
}
