namespace Syren.Server.Configuration;

public class SpeakersOptions
{
    public const string SectionName = "SpeakersOptions";

    /// <summary>
    /// Info per speaker
    /// </summary>
    public SpeakerInfo[] SpeakersInfo { get; set; } = [];
}

public readonly struct SpeakerInfo
{
    /// <summary>
    /// Associated SyrenSensor ID
    /// </summary>
    public required string SensorId { get; init; }

    /// <summary>
    /// Associated SnapCast client ID
    /// </summary>
    public required string SnapClientId { get; init; }

    /// <summary>
    /// Distance (in mm) for which any closer distance will entail full volume
    /// </summary>
    public required double FullVolumeDistance { get; init; }

    /// <summary>
    /// Distance (in mm) for which any farther distance will entail muting
    /// </summary>
    public required double MuteDistance { get; init; }
}
