using System.Numerics;
using Syren.Server.Models;

namespace Syren.Server.Services;

public interface IDistanceService
{
    public Task UpdateDistanceAsync(DistanceData distance);
    public Task UpdateDistancesAsync(IReadOnlyCollection<DistanceData> distances);
    public Task SetSpeakerVolumeAsync(string sensorId, double volume);

    public Task<SpeakerState?> ConnectSpeakerAsync(string sensorId);
    public Task DisconnectSpeakerAsync(string sensorId);

    public Vector3? GetUserPosition();
}
