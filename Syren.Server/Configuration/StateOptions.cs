namespace Syren.Server.Configuration;

public sealed class StateOptions
{
    public const string SectionName = "State";

    public string FilePath { get; set; } = "/var/lib/syren-server/state.json";
}
