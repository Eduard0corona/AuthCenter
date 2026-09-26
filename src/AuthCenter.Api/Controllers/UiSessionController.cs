using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using AuthCenter.Api.Authorization;
using AuthCenter.Api.Extensions;
using AuthCenter.Api.Middleware;
using AuthCenter.Application.Common;
using AuthCenter.Application.Interfaces;
using AuthCenter.Contracts.Requests.Auth;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Responses.Auth;
using AuthCenter.Domain.Constants;
using AuthCenter.Infrastructure.Settings;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace AuthCenter.Api.Controllers;

[ApiController]
[Route("ui-api/session")]
public sealed class UiSessionController : ControllerBase
{
    private readonly IAuthService _auth;
    private readonly IAccountManagementService _accounts;
    private readonly IPasskeyService _passkeys;
    private readonly IRefreshTokenService _refreshTokens;
    private readonly MfaSettings _mfa;
    private readonly SingleSignOnSettings _sso;

    public UiSessionController(
        IAuthService auth,
        IAccountManagementService accounts,
        IPasskeyService passkeys,
        IRefreshTokenService refreshTokens,
        IOptions<MfaSettings> mfa,
        IOptions<SingleSignOnSettings> sso)
    {
        _auth = auth;
        _accounts = accounts;
        _passkeys = passkeys;
        _refreshTokens = refreshTokens;
        _mfa = mfa.Value;
        _sso = sso.Value;
    }

    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingExtensions.Login)]
    [HttpPost("passkey/options")]
    public async Task<IActionResult> PasskeyOptions([FromBody] BeginPasskeyLoginRequest request, CancellationToken ct)
    {
        var result = await _passkeys.GetLoginOptionsAsync(request, ct);
        return result.IsSuccess
            ? Ok(ApiResponse<object>.Ok(result.Data!))
            : BadRequest(ApiResponse<object>.Fail(result.ErrorCode, result.Message));
    }

    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingExtensions.Login)]
    [HttpPost("passkey/complete")]
    public async Task<IActionResult> PasskeyComplete([FromBody] CompletePasskeyLoginRequest request, CancellationToken ct)
    {
        var result = await _passkeys.LoginAsync(request, IpAddress(), Request.Headers.UserAgent.ToString(), ct);
        if (!result.IsSuccess)
            return Unauthorized(ApiResponse<object>.Fail(result.ErrorCode, result.Message));
        await CreateSessionAsync(result.Data!);
        return Ok(ApiResponse<object>.Ok(ToSession(result.Data!.User, UiCsrfMiddleware.IssueToken(Response))));
    }

    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingExtensions.Login)]
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken ct)
    {
        var result = await _auth.LoginAsync(request, IpAddress(), Request.Headers.UserAgent.ToString(), ct);
        return await CompleteInteractiveStepAsync(result);
    }

    /// <summary>
    /// Completes a sign-in that stopped because the account must replace its temporary password.
    /// The new password is set first and the application's access-policy and MFA gate still runs.
    /// </summary>
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingExtensions.ForcedChangePassword)]
    [HttpPost("forced-change")]
    public async Task<IActionResult> ForcedChange([FromBody] ForcedChangePasswordRequest request, CancellationToken ct)
    {
        var result = await _auth.ForcedChangePasswordAsync(request, IpAddress(), Request.Headers.UserAgent.ToString(), ct);
        if (!result.IsSuccess && result.ErrorCode is "WEAK_PASSWORD")
            return BadRequest(ApiResponse<object>.Fail(result.ErrorCode, result.Message, result.Details));
        return await CompleteInteractiveStepAsync(result);
    }

    private async Task<IActionResult> CompleteInteractiveStepAsync(OperationResult<AuthResponse> result)
    {
        if (!result.IsSuccess && result.ErrorCode == "MFA_REQUIRED")
            return Ok(ApiResponse<object>.Ok(new { requiresMfa = true, mfaPendingToken = result.Message, expiresIn = _mfa.MfaTokenExpirySeconds }));
        if (!result.IsSuccess && result.ErrorCode == "PASSWORD_CHANGE_REQUIRED")
            return Ok(ApiResponse<object>.Ok(new { requiresPasswordChange = true, passwordChangeToken = result.Message, expiresIn = _mfa.MfaTokenExpirySeconds }));
        if (!result.IsSuccess)
            return Unauthorized(ApiResponse<object>.Fail(result.ErrorCode, result.Message));
        await CreateSessionAsync(result.Data!);
        return Ok(ApiResponse<object>.Ok(ToSession(result.Data!.User, UiCsrfMiddleware.IssueToken(Response))));
    }

    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingExtensions.MfaVerify)]
    [HttpPost("mfa")]
    public async Task<IActionResult> VerifyMfa([FromBody] VerifyMfaRequest request, CancellationToken ct)
    {
        var result = await _auth.VerifyMfaAsync(request, IpAddress(), Request.Headers.UserAgent.ToString(), ct);
        if (!result.IsSuccess)
            return Unauthorized(ApiResponse<object>.Fail(result.ErrorCode, result.Message));
        await CreateSessionAsync(result.Data!);
        return Ok(ApiResponse<object>.Ok(ToSession(result.Data!.User, UiCsrfMiddleware.IssueToken(Response))));
    }

    [Authorize]
    [HttpGet]
    public IActionResult Current()
    {
        var userId = User.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Ok(ApiResponse<object>.Ok(new
        {
            user = new
            {
                id = userId,
                name = User.FindFirstValue(JwtRegisteredClaimNames.Name) ?? User.FindFirstValue(ClaimTypes.Name),
                email = User.FindFirstValue(JwtRegisteredClaimNames.Email) ?? User.FindFirstValue(ClaimTypes.Email),
                applications = User.FindAll(DomainConstants.Claims.Applications).Select(item => item.Value).Distinct().ToArray(),
                roles = User.FindAll(ClaimTypes.Role).Select(item => item.Value).Distinct().ToArray(),
                permissions = User.FindAll(DomainConstants.Claims.Permissions).Select(item => item.Value).Distinct().ToArray()
            },
            csrfToken = UiCsrfMiddleware.IssueToken(Response)
        }));
    }

    [Authorize]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        var subject = User.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        var session = User.FindFirstValue(JwtRegisteredClaimNames.Sid) ?? User.FindFirstValue("sid");
        if (Guid.TryParse(subject, out var userId) && Guid.TryParse(session, out var sessionId))
            await _accounts.RevokeSessionAsync(userId, sessionId, ct);
        await HttpContext.SignOutAsync(AuthenticationSchemes.UiCookie);
        Response.Cookies.Delete(UiCsrfMiddleware.CookieName, new CookieOptions { Secure = true, SameSite = SameSiteMode.Strict, Path = "/" });
        return Ok(ApiResponse.Ok("Signed out."));
    }

    private async Task CreateSessionAsync(AuthResponse response)
    {
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(response.AccessToken);
        var userId = Guid.Parse(jwt.Subject);
        var newSessionId = Guid.Parse(jwt.Claims.Single(claim => claim.Type == JwtRegisteredClaimNames.Sid).Value);
        var currentSessionId = string.Equals(User.Identity?.AuthenticationType, AuthenticationSchemes.UiCookie, StringComparison.Ordinal)
            ? SessionClaims.SessionId(User)
            : null;
        var sessionId = await _refreshTokens.ContinueBrowserSessionAsync(currentSessionId, userId, newSessionId, HttpContext.RequestAborted);
        var claims = jwt.Claims
            .Where(claim => claim.Type is not "exp" and not "nbf" and not "iat" and not JwtRegisteredClaimNames.Sid)
            .Select(claim => claim.Type == DomainConstants.Claims.Role ? new Claim(ClaimTypes.Role, claim.Value) : claim)
            .Append(new Claim(JwtRegisteredClaimNames.Sid, sessionId.ToString()))
            .ToList();
        var identity = new ClaimsIdentity(claims, AuthenticationSchemes.UiCookie, JwtRegisteredClaimNames.Name, ClaimTypes.Role);
        await HttpContext.SignInAsync(AuthenticationSchemes.UiCookie, new ClaimsPrincipal(identity), new AuthenticationProperties
        {
            IsPersistent = false,
            AllowRefresh = false,
            // The single sign-on session outlives the short access token it was created from; the
            // session record behind "sid" is still checked on every request and can be revoked.
            ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(_sso.SessionLifetimeMinutes)
        });
    }

    private static object ToSession(AuthenticatedUserDto user, string csrfToken) => new { user, csrfToken };
    private string? IpAddress() => HttpContext.Connection.RemoteIpAddress?.ToString();
}
