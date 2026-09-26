namespace Syren.Server.Configuration;

public sealed class PlaybackOptions
{
    public const string SectionName = "Playback";

    public AudioSourceOption[] Sources { get; set; } = [];

    public string StreamPrefix { get; set; } = "syren-priority";
    public string SampleFormat { get; set; } = "48000:16:2";
    public string Codec { get; set; } = "pcm";
    public int ReconcileSeconds { get; set; } = 5;
    public bool ProfileSessions { get; set; }
}

public sealed class AudioSourceOption
{
    public required string Id { get; init; }
    public required string Name { get; init; }
}
