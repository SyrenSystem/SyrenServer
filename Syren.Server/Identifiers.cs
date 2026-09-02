namespace Syren.Server;

public static class Identifiers
{
    public static string Normalize(string id) => id.Trim().ToLowerInvariant();

    public static string? NormalizeOptional(string? id) =>
        string.IsNullOrWhiteSpace(id) ? null : Normalize(id);
}
