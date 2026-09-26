using System.Text.Json.Nodes;

namespace Syren.Server.Services;

// Version 3 names a group's sources enabledSources and always uses manual volume.
internal static class ProfileGroupSchema
{
    public static void ToProfileNames(JsonNode? groups)
    {
        foreach (JsonObject group in Groups(groups))
        {
            group["enabledSources"] = Take(group, "sourcePriority");
            group.Remove("volumeMode");
        }
    }

    public static void FromProfileNames(JsonNode? groups)
    {
        foreach (JsonObject group in Groups(groups))
        {
            FromProfileNames(group);
        }
    }

    public static JsonObject FromProfileNames(JsonObject group)
    {
        group["sourcePriority"] = Take(group, "enabledSources");
        group["volumeMode"] = "manual";
        return group;
    }

    private static JsonNode Take(JsonObject group, string property)
    {
        if (!group.TryGetPropertyValue(property, out JsonNode? value) || value is not JsonArray)
        {
            throw new InvalidDataException($"A playback group is missing {property}");
        }
        group.Remove(property);
        return value;
    }

    private static IEnumerable<JsonObject> Groups(JsonNode? groups) => (groups as JsonArray ?? [])
        .Select(group => group as JsonObject ?? throw new InvalidDataException("A playback group must be an object"));
}
