using AuthCenter.Infrastructure.Security;
using Microsoft.Extensions.Options;

namespace AuthCenter.Infrastructure.Settings;

public class JwtSettingsValidator : IValidateOptions<JwtSettings>
{
    public ValidateOptionsResult Validate(string? name, JwtSettings options)
    {
        var errors = new List<string>();
        var error = RsaSigningKeyRing.DescribeConfigurationError(options);

        if (error is not null)
            errors.Add(error);
        if (string.IsNullOrWhiteSpace(options.Issuer))
            errors.Add("Jwt:Issuer is required.");
        if (string.IsNullOrWhiteSpace(options.Audience))
            errors.Add("Jwt:Audience is required.");
        if (options.AccessTokenMinutes is < 1 or > 60)
            errors.Add("Jwt:AccessTokenMinutes must be between 1 and 60.");
        if (options.RefreshTokenDays is < 1 or > 90)
            errors.Add("Jwt:RefreshTokenDays must be between 1 and 90.");
        if (options.MagicLinkTokenMinutes is < 1 or > 30)
            errors.Add("Jwt:MagicLinkTokenMinutes must be between 1 and 30.");

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }
}
