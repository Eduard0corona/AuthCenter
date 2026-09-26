using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace AuthCenter.Client;

internal sealed class AuthCenterAccessTokenValidator(
    IOptionsMonitor<OpenIdConnectOptions> optionsMonitor,
    AuthCenterBffOptions bffOptions)
{
    private readonly JwtSecurityTokenHandler _handler = new() { MapInboundClaims = false };

    public async Task<ClaimsPrincipal> ValidateAsync(string accessToken, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        var oidc = optionsMonitor.Get(AuthCenterBffDefaults.OpenIdConnectScheme);
        var configurationManager = oidc.ConfigurationManager
            ?? throw new InvalidOperationException("The AuthCenter OIDC configuration manager is unavailable.");
        var configuration = await configurationManager.GetConfigurationAsync(cancellationToken);
        var result = await ValidateAsync(accessToken, oidc, configuration);
        if (!result.IsValid && result.Exception is SecurityTokenSignatureKeyNotFoundException)
        {
            configurationManager.RequestRefresh();
            configuration = await configurationManager.GetConfigurationAsync(cancellationToken);
            result = await ValidateAsync(accessToken, oidc, configuration);
        }

        if (!result.IsValid || result.ClaimsIdentity is null)
            throw new SecurityTokenException("The AuthCenter access token failed validation.", result.Exception);
        return new ClaimsPrincipal(result.ClaimsIdentity);
    }

    private Task<TokenValidationResult> ValidateAsync(
        string accessToken,
        OpenIdConnectOptions oidc,
        Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectConfiguration configuration)
    {
        var parameters = oidc.TokenValidationParameters.Clone();
        parameters.ValidIssuer = configuration.Issuer;
        parameters.ValidAudience = bffOptions.ClientId;
        parameters.IssuerSigningKeys = configuration.SigningKeys;
        parameters.ValidateIssuer = true;
        parameters.ValidateAudience = true;
        parameters.ValidateLifetime = true;
        parameters.ValidateIssuerSigningKey = true;
        parameters.ValidAlgorithms = [SecurityAlgorithms.RsaSha256];
        parameters.ValidTypes = [AuthCenterBffDefaults.AccessTokenType];

        return _handler.ValidateTokenAsync(accessToken, parameters);
    }
}
