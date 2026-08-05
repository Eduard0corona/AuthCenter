using AuthCenter.Api.Authorization;
using AuthCenter.Application.Common;
using AuthCenter.Application.Interfaces;
using AuthCenter.Contracts.Requests.OAuth;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Responses.OAuth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

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
    [Consumes("application/x-www-form-urlencoded")]
    public async Task<IActionResult> Token(CancellationToken ct)
    {
        var form = await Request.ReadFormAsync(ct);
        var request = new OAuthTokenRequest
        {
            GrantType = form["grant_type"],
            Code = form["code"],
            RedirectUri = form["redirect_uri"],
            ClientId = form["client_id"],
            ClientSecret = form["client_secret"],
            CodeVerifier = form["code_verifier"],
            Scope = form["scope"],
            RefreshToken = form["refresh_token"]
        };

        OperationResult<OAuthTokenResponse> result = request.GrantType switch
        {
            "authorization_code" => await _oAuthService.ExchangeCodeAsync(request, ct),
            "client_credentials" => await _oAuthService.ClientCredentialsAsync(request, ct),
            "refresh_token"      => await _oAuthService.RefreshOAuthTokenAsync(request, ct),
            _ => OperationResult<OAuthTokenResponse>.Failure(
                "UNSUPPORTED_GRANT_TYPE",
                $"Grant type '{request.GrantType}' is not supported.")
        };

        if (!result.IsSuccess)
            return BadRequest(new { error = result.ErrorCode.ToLowerInvariant(), error_description = result.Message });

        return Ok(result.Data!);
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

        var result = await _oAuthService.GetUserInfoAsync(userId.Value, scopes, ct);
        if (!result.IsSuccess)
            return BadRequest(ApiResponse<object>.Fail(result.ErrorCode, result.Message));
        return Ok(result.Data!);
    }
}
