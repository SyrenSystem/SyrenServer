namespace Syren.Server.Configuration;

public class SyrenSettings
{
    public const string SectionName = "SyrenSettings";

    /// <summary>
    /// Smoothing factor alpha for each new distance datapoint acquired:
    /// distance = newDataPoint * alpha + distance * (1.0 - alpha)
    /// </summary>
    public double DistanceSmoothingFactor { get; set; } = 0.3;
}
