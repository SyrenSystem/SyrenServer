using Microsoft.Extensions.Options;

namespace Syren.Server.Configuration;

public sealed class SnapCastOptionsValidator : IValidateOptions<SnapCastOptions>
{
    public ValidateOptionsResult Validate(string? name, SnapCastOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ServerHost))
        {
            return ValidateOptionsResult.Fail("Snapserver host is required");
        }
        if (options.HttpPort is < 1 or > 65535)
        {
            return ValidateOptionsResult.Fail("Snapserver port must be between 1 and 65535");
        }
        if (options.RequestTimeoutSeconds is < 1 or > 30)
        {
            return ValidateOptionsResult.Fail("Snapserver timeout must be between 1 and 30 seconds");
        }
        return ValidateOptionsResult.Success;
    }
}
