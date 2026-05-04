using System.IdentityModel.Tokens.Jwt;
using AuthCenter.Application.Interfaces;
using AuthCenter.Application.Models;
using AuthCenter.Infrastructure.Settings;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace AuthCenter.Infrastructure.Services;

public class AppleAuthService : IAppleAuthService
{
    private static readonly ConfigurationManager<OpenIdConnectConfiguration> AppleConfigManager =
        new(
            "https://appleid.apple.com/.well-known/openid-configuration",
            new OpenIdConnectConfigurationRetriever());

    private readonly AppleAuthSettings _settings;

    public AppleAuthService(IOptions<AppleAuthSettings> settings)
    {
        _settings = settings.Value;
    }

    public async Task<ExternalTokenPayload?> ValidateIdTokenAsync(string idToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.ClientId))
            return null;

        try
        {
            var config = await AppleConfigManager.GetConfigurationAsync(ct);
            var handler = new JwtSecurityTokenHandler();
            handler.InboundClaimTypeMap.Clear();

            var principal = handler.ValidateToken(idToken, new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = "https://appleid.apple.com",
                ValidateAudience = true,
                ValidAudiences = [_settings.ClientId],
                ValidateLifetime = true,
                IssuerSigningKeys = config.SigningKeys,
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                ClockSkew = TimeSpan.FromMinutes(2)
            }, out _);

            var subject = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
            var email = principal.FindFirst("email")?.Value;

            if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(email))
                return null;

            return new ExternalTokenPayload
            {
                Subject = subject,
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
}
