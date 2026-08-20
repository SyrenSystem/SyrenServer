using Microsoft.Extensions.Options;

namespace Syren.Server.Configuration;

public sealed class StateOptionsValidator : IValidateOptions<StateOptions>
{
    public ValidateOptionsResult Validate(string? name, StateOptions options) =>
        string.IsNullOrWhiteSpace(options.FilePath)
            ? ValidateOptionsResult.Fail("System state file path is required")
            : ValidateOptionsResult.Success;
}
