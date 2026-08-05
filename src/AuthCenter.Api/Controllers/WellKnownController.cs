using AuthCenter.Application.Interfaces;
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

    public WellKnownController(IOptions<JwtSettings> jwtSettings, ITokenService tokenService)
    {
        _jwtSettings = jwtSettings.Value;
        _tokenService = tokenService;
    }

    [HttpGet("openid-configuration")]
    public IActionResult OpenIdConfiguration()
    {
        // The issuer claim must keep matching the "iss" of the tokens we sign, but the endpoint
        // URLs have to be absolute and reachable, so they are built from the request base address
        // instead of from the issuer (which is not necessarily a URL).
        var issuer = _jwtSettings.Issuer;
        var baseUrl = $"{Request.Scheme}://{Request.Host}{Request.PathBase}".TrimEnd('/');
        var discovery = new
        {
            issuer,
            authorization_endpoint = $"{baseUrl}/oauth/authorize",
            token_endpoint = $"{baseUrl}/oauth/token",
            userinfo_endpoint = $"{baseUrl}/oauth/userinfo",
            jwks_uri = $"{baseUrl}/.well-known/jwks.json",
            scopes_supported = new[] { "openid", "profile", "email", "offline_access" },
            response_types_supported = new[] { "code" },
            grant_types_supported = new[] { "authorization_code", "client_credentials", "refresh_token" },
            subject_types_supported = new[] { "public" },
            id_token_signing_alg_values_supported = new[] { "RS256" },
            token_endpoint_auth_methods_supported = new[] { "client_secret_post", "none" }
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
