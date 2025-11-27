namespace Syren.Server.Configuration;

public class SnapCastOptions
{
    public const string SectionName = "SnapCast";

    /// <summary>
    /// SnapServer host address  (default: localhost)
    /// </summary>
    public string ServerHost { get; set; } = "localhost";

    /// <summary>
    /// SnapServer HTTP port (default: 1780)
    /// </summary>
    public int HttpPort { get; set; } = 1780;
}
