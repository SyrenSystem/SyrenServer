using Microsoft.Extensions.Options;

namespace Syren.Server.Configuration;

public sealed class PlaybackOptionsValidator : IValidateOptions<PlaybackOptions>
{
    public ValidateOptionsResult Validate(string? name, PlaybackOptions options)
    {
        var failures = new List<string>();
        var sourceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (AudioSourceOption source in options.Sources)
        {
            if (string.IsNullOrWhiteSpace(source.Id) || !sourceIds.Add(source.Id))
            {
                failures.Add("Playback source IDs must be nonempty and unique");
            }
            if (string.IsNullOrWhiteSpace(source.Name))
            {
                failures.Add("Playback source names must be nonempty");
            }
        }
        if (options.Sources.Length == 0)
        {
            failures.Add("At least one playback source is required");
        }
        if (string.IsNullOrWhiteSpace(options.StreamPrefix) ||
            string.IsNullOrWhiteSpace(options.SampleFormat) ||
            string.IsNullOrWhiteSpace(options.Codec))
        {
            failures.Add("Playback stream settings must be nonempty");
        }
        if (options.ReconcileSeconds is < 1 or > 300)
        {
            failures.Add("Playback reconcile seconds must be between 1 and 300");
        }
        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures.Distinct());
    }
}
