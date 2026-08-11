using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AuthCenter.Infrastructure.Settings;

public sealed class PasskeySettingsValidator : IValidateOptions<PasskeySettings>
{
    private readonly IHostEnvironment _environment;

    public PasskeySettingsValidator(IHostEnvironment environment)
    {
        _environment = environment;
    }

    public ValidateOptionsResult Validate(string? name, PasskeySettings settings)
    {
        if (settings.CeremonyMinutes is < 1 or > 10)
            return ValidateOptionsResult.Fail("Passkeys:CeremonyMinutes must be between 1 and 10.");
        if (settings.MaxCredentialsPerUser is < 2 or > 20)
            return ValidateOptionsResult.Fail("Passkeys:MaxCredentialsPerUser must be between 2 and 20.");

        if (_environment.IsDevelopment() || _environment.IsEnvironment("Testing"))
            return ValidateOptionsResult.Success;

        if (string.IsNullOrWhiteSpace(settings.RelyingPartyId) || settings.RelyingPartyId.Contains("REPLACE_WITH_", StringComparison.OrdinalIgnoreCase))
            return ValidateOptionsResult.Fail("Passkeys:RelyingPartyId is required outside Development/Testing.");
        if (settings.AllowedOrigins.Length == 0)
            return ValidateOptionsResult.Fail("Passkeys:AllowedOrigins must contain at least one HTTPS origin.");
        if (settings.AllowedOrigins.Any(origin => origin.Contains("REPLACE_WITH_", StringComparison.OrdinalIgnoreCase) || !Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.PathAndQuery != "/"))
            return ValidateOptionsResult.Fail("Passkeys:AllowedOrigins must contain HTTPS origins without paths or query strings.");

        return ValidateOptionsResult.Success;
    }
}
