namespace Syren.Server.Models;

public struct Speaker
{
    public required string SensorId;
    public required string SnapClientId;

    public required double FullVolumeDistance;
    public required double MuteDistance;
}
