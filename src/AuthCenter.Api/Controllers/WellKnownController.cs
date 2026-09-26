using AuthCenter.Application.Interfaces;
using AuthCenter.Domain.Constants;
using AuthCenter.Infrastructure.Settings;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace AuthCenter.Api.Controllers;

[ApiController]
[Route(".well-known")]
public class WellKnownController : ControllerBase
{
    private readonly JwtSettings _jwtSettings;
    private readonly ITokenService _tokenService;
    private readonly string _publicOrigin;

    public WellKnownController(IOptions<JwtSettings> jwtSettings, ITokenService tokenService, IConfiguration configuration)
    {
        _jwtSettings = jwtSettings.Value;
        _tokenService = tokenService;
        _publicOrigin = configuration["Oidc:PublicOrigin"]?.TrimEnd('/') ?? string.Empty;
    }

    [HttpGet("openid-configuration")]
    public IActionResult OpenIdConfiguration()
    {
        var issuer = _jwtSettings.Issuer;
        var baseUrl = _publicOrigin;
        if (string.IsNullOrWhiteSpace(baseUrl))
            return Problem("OIDC public origin is not configured.", statusCode: StatusCodes.Status503ServiceUnavailable);
        var discovery = new
        {
            issuer,
            authorization_endpoint = $"{baseUrl}/oauth/authorize",
            token_endpoint = $"{baseUrl}/oauth/token",
            revocation_endpoint = $"{baseUrl}/oauth/revoke",
            userinfo_endpoint = $"{baseUrl}/oauth/userinfo",
            jwks_uri = $"{baseUrl}/.well-known/jwks.json",
            scopes_supported = new[] { "openid", "profile", "email", "offline_access" },
            response_types_supported = new[] { "code" },
            grant_types_supported = new[] { "authorization_code", "client_credentials", "refresh_token" },
            subject_types_supported = new[] { "public" },
            id_token_signing_alg_values_supported = new[] { "RS256" },
            token_endpoint_auth_methods_supported = new[] { "client_secret_basic", "client_secret_post", "none" },
            revocation_endpoint_auth_methods_supported = new[] { "client_secret_basic", "client_secret_post", "none" },
            code_challenge_methods_supported = new[] { "S256" },
            authorization_response_iss_parameter_supported = true,
            response_modes_supported = new[] { "query", "form_post" },
            prompt_values_supported = new[] { "none", "login", "consent", "select_account" },
            acr_values_supported = DomainConstants.AuthenticationContextClasses.All,
            claims_supported = new[]
            {
                "sub", "iss", "aud", "exp", "iat", "auth_time", "nonce", "azp", "sid", "amr", "acr", "name",
                "email", "email_verified", "client_id", "scope", "role", "permissions", "applications"
            },
            request_parameter_supported = false,
            request_uri_parameter_supported = false,
            claims_parameter_supported = false
        };
        return Ok(discovery);
    }

    [HttpGet("jwks.json")]
    public IActionResult Jwks()
    {
        var json = _tokenService.GetJwks();
        return Content(json, "application/json");
    }
}
