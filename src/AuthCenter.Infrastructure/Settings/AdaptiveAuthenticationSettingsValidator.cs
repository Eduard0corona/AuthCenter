using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AuthCenter.Infrastructure.Settings;

public sealed class AdaptiveAuthenticationSettingsValidator : IValidateOptions<AdaptiveAuthenticationSettings>
{
    private readonly IHostEnvironment _environment;
    public AdaptiveAuthenticationSettingsValidator(IHostEnvironment environment) => _environment = environment;

    public ValidateOptionsResult Validate(string? name, AdaptiveAuthenticationSettings settings)
    {
        if (settings.ObservationRetentionDays is < 1 or > 90 || settings.FailedEventThreshold is < 3 or > 20 || settings.MaximumTravelSpeedKmh is < 300 or > 2000)
            return ValidateOptionsResult.Fail("AdaptiveAuth retention, failed-event threshold, or travel-speed settings are outside safe bounds.");
        if (!_environment.IsDevelopment() && !_environment.IsEnvironment("Testing") &&
            (settings.SignalHashKey.Length < 32 || settings.SignalHashKey.Contains("REPLACE_WITH_", StringComparison.OrdinalIgnoreCase)))
            return ValidateOptionsResult.Fail("AdaptiveAuth:SignalHashKey must be a secret of at least 32 characters outside Development/Testing.");
        return ValidateOptionsResult.Success;
    }
}
