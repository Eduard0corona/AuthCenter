using AuthCenter.Api.Authorization;
using AuthCenter.Api.Extensions;
using AuthCenter.Application.Common;
using AuthCenter.Application.Interfaces;
using AuthCenter.Contracts.Requests.OAuth;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Responses.OAuth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using System.Text;

namespace AuthCenter.Api.Controllers;

[ApiController]
[Route("oauth")]
public class OAuthController : ControllerBase
{
    private readonly IOAuthAuthorizationService _oAuthService;
    private readonly ICurrentUserService _currentUserService;

    public OAuthController(IOAuthAuthorizationService oAuthService, ICurrentUserService currentUserService)
    {
        _oAuthService = oAuthService;
        _currentUserService = currentUserService;
    }

    [HttpGet("authorize")]
    public async Task<IActionResult> Authorize(CancellationToken ct)
    {
        var q = Request.Query;
        var request = new AuthorizeRequest
        {
            ResponseType = q["response_type"],
            ClientId = q["client_id"],
            RedirectUri = q["redirect_uri"],
            Scope = q["scope"],
            State = q["state"],
            CodeChallenge = q["code_challenge"],
            CodeChallengeMethod = q["code_challenge_method"],
            Nonce = q["nonce"]
        };

        var result = await _oAuthService.InitiateAuthorizationAsync(request, ct);
        if (!result.IsSuccess)
            return BadRequest(ApiResponse<object>.Fail(result.ErrorCode, result.Message));
        return Redirect(result.Data!);
    }

    [Authorize]
    [HttpGet("interactions/{interactionId}")]
    public async Task<IActionResult> GetInteraction(string interactionId, CancellationToken ct)
    {
        var result = await _oAuthService.GetInteractionAsync(interactionId, ct);
        if (!result.IsSuccess)
            return BadRequest(ApiResponse<object>.Fail(result.ErrorCode, result.Message));
        return Ok(ApiResponse<object>.Ok(result.Data!));
    }

    [Authorize]
    [HttpPost("authorize/complete")]
    public async Task<IActionResult> CompleteAuthorization([FromBody] CompleteAuthorizationRequest request, CancellationToken ct)
    {
        var userId = _currentUserService.UserId;
        if (userId is null) return Unauthorized();

        var result = await _oAuthService.CompleteAuthorizationAsync(request, userId.Value, ct);
        if (!result.IsSuccess)
            return BadRequest(ApiResponse<object>.Fail(result.ErrorCode, result.Message));
        return Redirect(result.Data!);
    }

    [HttpPost("token")]
    [EnableRateLimiting(RateLimitingExtensions.OAuthToken)]
    [Consumes("application/x-www-form-urlencoded")]
    public async Task<IActionResult> Token(CancellationToken ct)
    {
        var form = await Request.ReadFormAsync(ct);
        var credentials = ReadClientCredentials(form);
        if (!credentials.IsValid)
            return OAuthError("invalid_client", credentials.Error!, StatusCodes.Status401Unauthorized);

        var request = new OAuthTokenRequest
        {
            GrantType = form["grant_type"],
            Code = form["code"],
            RedirectUri = form["redirect_uri"],
            ClientId = credentials.ClientId,
            ClientSecret = credentials.ClientSecret,
            CodeVerifier = form["code_verifier"],
            Scope = form["scope"],
            RefreshToken = form["refresh_token"]
        };

        OperationResult<OAuthTokenResponse> result = request.GrantType switch
        {
            "authorization_code" => await _oAuthService.ExchangeCodeAsync(request, ct),
            "client_credentials" => await _oAuthService.ClientCredentialsAsync(request, ct),
            "refresh_token" => await _oAuthService.RefreshOAuthTokenAsync(request, ct),
            _ => OperationResult<OAuthTokenResponse>.Failure(
                "UNSUPPORTED_GRANT_TYPE",
                $"Grant type '{request.GrantType}' is not supported.")
        };

        if (!result.IsSuccess)
            return OAuthError(
                result.ErrorCode.ToLowerInvariant(),
                result.Message,
                result.ErrorCode is "INVALID_CLIENT" or "INVALID_CLIENT_CREDENTIALS"
                    ? StatusCodes.Status401Unauthorized
                    : StatusCodes.Status400BadRequest);

        return Ok(result.Data!);
    }

    [HttpPost("revoke")]
    [EnableRateLimiting(RateLimitingExtensions.OAuthToken)]
    [Consumes("application/x-www-form-urlencoded")]
    public async Task<IActionResult> Revoke(CancellationToken ct)
    {
        var form = await Request.ReadFormAsync(ct);
        var credentials = ReadClientCredentials(form);
        if (!credentials.IsValid)
            return OAuthError("invalid_client", credentials.Error!, StatusCodes.Status401Unauthorized);

        var result = await _oAuthService.RevokeTokenAsync(new OAuthRevocationRequest
        {
            Token = form["token"],
            TokenTypeHint = form["token_type_hint"],
            ClientId = credentials.ClientId,
            ClientSecret = credentials.ClientSecret
        }, ct);

        if (!result.IsSuccess)
            return OAuthError(
                result.ErrorCode.ToLowerInvariant(),
                result.Message,
                result.ErrorCode is "INVALID_CLIENT" or "INVALID_CLIENT_CREDENTIALS"
                    ? StatusCodes.Status401Unauthorized
                    : StatusCodes.Status400BadRequest);

        return Ok();
    }

    [Authorize(AuthenticationSchemes = AuthenticationSchemes.OAuthBearer)]
    [HttpGet("userinfo")]
    public async Task<IActionResult> UserInfo(CancellationToken ct)
    {
        // Only tokens minted by the token endpoint carry client_id; this keeps first-party login
        // tokens, which are not audience-scoped to a client, out of the OAuth userinfo endpoint.
        if (User.FindFirst("client_id") is null) return Unauthorized();

        var userId = _currentUserService.UserId;
        if (userId is null) return Unauthorized();

        var scopeClaim = User.FindFirst("scope")?.Value ?? string.Empty;
        var scopes = scopeClaim.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var clientId = User.FindFirst("client_id")?.Value;
        if (string.IsNullOrWhiteSpace(clientId)) return Unauthorized();

        var result = await _oAuthService.GetUserInfoAsync(userId.Value, clientId, scopes, ct);
        if (!result.IsSuccess)
            return Unauthorized(ApiResponse<object>.Fail(result.ErrorCode, result.Message));
        return Ok(result.Data!);
    }

    private (bool IsValid, string? ClientId, string? ClientSecret, string? Error) ReadClientCredentials(IFormCollection form)
    {
        var formClientId = form["client_id"].ToString();
        var formClientSecret = form["client_secret"].ToString();
        var authorization = Request.Headers.Authorization.ToString();

        if (string.IsNullOrWhiteSpace(authorization))
            return (true, formClientId, formClientSecret, null);

        if (!authorization.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            return (false, null, null, "Only HTTP Basic client authentication is supported.");

        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(authorization[6..].Trim()));
            var separator = decoded.IndexOf(':');
            if (separator <= 0)
                return (false, null, null, "Malformed HTTP Basic client credentials.");

            var basicClientId = Uri.UnescapeDataString(decoded[..separator]);
            var basicSecret = Uri.UnescapeDataString(decoded[(separator + 1)..]);
            if (!string.IsNullOrWhiteSpace(formClientId) && !string.Equals(formClientId, basicClientId, StringComparison.Ordinal))
                return (false, null, null, "Conflicting client_id credentials were supplied.");
            if (!string.IsNullOrWhiteSpace(formClientSecret))
                return (false, null, null, "Use only one client authentication method per request.");

            return (true, basicClientId, basicSecret, null);
        }
        catch (Exception exception) when (exception is FormatException or UriFormatException)
        {
            return (false, null, null, "Malformed HTTP Basic client credentials.");
        }
    }

    private ObjectResult OAuthError(string error, string description, int statusCode)
    {
        if (statusCode == StatusCodes.Status401Unauthorized)
            Response.Headers.WWWAuthenticate = "Basic realm=\"oauth/token\"";

        return StatusCode(statusCode, new { error, error_description = description });
    }
}
