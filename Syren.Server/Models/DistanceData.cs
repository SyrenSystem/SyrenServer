namespace Syren.Server.Models;

public readonly struct DistanceData
{
    public required uint SpeakerId { get; init; }
    public required double Distance { get; init; }
}
