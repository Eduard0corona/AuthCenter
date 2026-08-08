using Microsoft.Extensions.Options;

namespace AuthCenter.Infrastructure.Settings;

public sealed class MfaSettingsValidator : IValidateOptions<MfaSettings>
{
    public ValidateOptionsResult Validate(string? name, MfaSettings options)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(options.EncryptionKey) || options.EncryptionKey.Length < 32)
            errors.Add("Mfa:EncryptionKey must contain at least 32 characters.");
        if (string.IsNullOrWhiteSpace(options.TotpIssuer))
            errors.Add("Mfa:TotpIssuer is required.");
        if (options.MfaTokenExpirySeconds is < 60 or > 900)
            errors.Add("Mfa:MfaTokenExpirySeconds must be between 60 and 900 seconds.");
        if (options.PreviousEncryptionKeys.Any(key => string.IsNullOrWhiteSpace(key) || key.Length < 32))
            errors.Add("Every Mfa:PreviousEncryptionKeys entry must contain at least 32 characters.");

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }
}
