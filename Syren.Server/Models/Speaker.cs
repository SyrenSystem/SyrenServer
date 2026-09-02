namespace Syren.Server.Models;

public sealed class Speaker
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public string? SensorId { get; set; }
    public required string SnapClientId { get; set; }

    public required double FullVolumeDistance { get; set; }
    public required double MuteDistance { get; set; }
}
