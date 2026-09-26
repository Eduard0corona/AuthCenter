using Microsoft.Extensions.Options;

namespace AuthCenter.Infrastructure.Settings;

public sealed class SingleSignOnSettingsValidator : IValidateOptions<SingleSignOnSettings>
{
    public ValidateOptionsResult Validate(string? name, SingleSignOnSettings options) =>
        options.SessionLifetimeMinutes is < 5 or > 10_080
            ? ValidateOptionsResult.Fail("Sso:SessionLifetimeMinutes must be between 5 minutes and 7 days.")
            : ValidateOptionsResult.Success;
}
