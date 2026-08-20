using Microsoft.Extensions.Options;

namespace Syren.Server.Configuration;

public sealed class SpeakersOptionsValidator : IValidateOptions<SpeakersOptions>
{
    public ValidateOptionsResult Validate(string? name, SpeakersOptions options)
    {
        var failures = new List<string>();
        var sensorIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var snapClientIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (SpeakerInfo speaker in options.SpeakersInfo)
        {
            if (string.IsNullOrWhiteSpace(speaker.SensorId) || !sensorIds.Add(speaker.SensorId))
            {
                failures.Add("Speaker sensor IDs must be nonempty and unique");
            }
            if (string.IsNullOrWhiteSpace(speaker.SnapClientId) ||
                !snapClientIds.Add(speaker.SnapClientId))
            {
                failures.Add("Snapclient IDs must be nonempty and unique");
            }
            if (!double.IsFinite(speaker.FullVolumeDistance) || speaker.FullVolumeDistance < 0)
            {
                failures.Add("Full volume distances must be finite and nonnegative");
            }
            if (!double.IsFinite(speaker.MuteDistance) ||
                speaker.MuteDistance <= speaker.FullVolumeDistance)
            {
                failures.Add("Mute distances must exceed full volume distances");
            }
        }
        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures.Distinct());
    }
}
