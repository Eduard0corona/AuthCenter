using AuthCenter.Infrastructure.Security;
using Microsoft.Extensions.Options;

namespace AuthCenter.Infrastructure.Settings;

public class JwtSettingsValidator : IValidateOptions<JwtSettings>
{
    public ValidateOptionsResult Validate(string? name, JwtSettings options)
    {
        var error = RsaSigningKeyRing.DescribeConfigurationError(options);

        return error is null
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(error);
    }
}
