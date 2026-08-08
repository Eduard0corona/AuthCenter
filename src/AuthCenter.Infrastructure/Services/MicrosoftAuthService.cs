using System.IdentityModel.Tokens.Jwt;
using AuthCenter.Application.Interfaces;
using AuthCenter.Application.Models;
using AuthCenter.Infrastructure.Settings;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace AuthCenter.Infrastructure.Services;

public class MicrosoftAuthService : IMicrosoftAuthService
{
    private static readonly ConfigurationManager<OpenIdConnectConfiguration> CommonConfigurationManager =
        new(
            "https://login.microsoftonline.com/common/v2.0/.well-known/openid-configuration",
            new OpenIdConnectConfigurationRetriever());

    private readonly MicrosoftAuthSettings _settings;

    public MicrosoftAuthService(IOptions<MicrosoftAuthSettings> settings)
    {
        _settings = settings.Value;
    }

    public async Task<ExternalTokenPayload?> ValidateIdTokenAsync(string idToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.ClientId))
            return null;

        try
        {
            var config = await CommonConfigurationManager.GetConfigurationAsync(ct);
            var handler = new JwtSecurityTokenHandler();
            handler.InboundClaimTypeMap.Clear();

            var parameters = new TokenValidationParameters
            {
                // Multi-tenant Microsoft tokens use a tenant-specific issuer derived from tid.
                ValidateIssuer = !IsMultiTenant(),
                ValidateAudience = true,
                ValidAudiences = [_settings.ClientId],
                ValidateLifetime = true,
                IssuerSigningKeys = config.SigningKeys,
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                ClockSkew = TimeSpan.FromMinutes(2)
            };

            if (!IsMultiTenant())
                parameters.ValidIssuer = $"https://login.microsoftonline.com/{_settings.TenantId}/v2.0";

            var principal = handler.ValidateToken(idToken, parameters, out _);

            var tenantId = principal.FindFirst("tid")?.Value;
            var issuer = principal.FindFirst(JwtRegisteredClaimNames.Iss)?.Value;
            if (IsMultiTenant())
            {
                if (!Guid.TryParse(tenantId, out _) ||
                    !string.Equals(
                        issuer,
                        $"https://login.microsoftonline.com/{tenantId}/v2.0",
                        StringComparison.Ordinal))
                {
                    return null;
                }
            }

            // preferred_username is mutable and may not even be an email address. It is suitable
            // for display hints, not as an account-linking identifier.
            var email = principal.FindFirst("email")?.Value;
            var subject = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;

            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(subject))
                return null;

            return new ExternalTokenPayload
            {
                Subject = IsMultiTenant() ? $"{tenantId}:{subject}" : subject,
                Email = email,
                Name = principal.FindFirst("name")?.Value,
                PictureUrl = null
            };
        }
        catch
        {
            return null;
        }
    }

    private bool IsMultiTenant() =>
        string.IsNullOrWhiteSpace(_settings.TenantId) ||
        string.Equals(_settings.TenantId, "common", StringComparison.OrdinalIgnoreCase);
}
