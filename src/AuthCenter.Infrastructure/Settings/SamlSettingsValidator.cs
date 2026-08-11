using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AuthCenter.Infrastructure.Settings;

public sealed class SamlSettingsValidator : IValidateOptions<SamlSettings>
{
    private readonly IHostEnvironment _environment;
    public SamlSettingsValidator(IHostEnvironment environment) => _environment = environment;
    public ValidateOptionsResult Validate(string? name, SamlSettings settings)
    {
        if (settings.ClockSkewSeconds is < 0 or > 300) return ValidateOptionsResult.Fail("Saml:ClockSkewSeconds must be between 0 and 300.");
        if (_environment.IsDevelopment() || _environment.IsEnvironment("Testing")) return ValidateOptionsResult.Success;
        if (!Uri.TryCreate(settings.EntityId, UriKind.Absolute, out _) || !Uri.TryCreate(settings.AssertionConsumerServiceUrl, UriKind.Absolute, out var acs) || acs.Scheme != Uri.UriSchemeHttps)
            return ValidateOptionsResult.Fail("Saml entity ID and HTTPS assertion consumer service URL are required.");
        try { _ = Convert.FromBase64String(settings.SigningCertificateBase64); }
        catch (FormatException) { return ValidateOptionsResult.Fail("Saml signing certificate must be supplied by Key Vault as base64 PKCS#12."); }
        return ValidateOptionsResult.Success;
    }
}
