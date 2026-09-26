using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using AuthCenter.Api.Authorization;
using AuthCenter.Api.Extensions;
using AuthCenter.Api.Middleware;
using AuthCenter.Application.Common;
using AuthCenter.Application.Interfaces;
using AuthCenter.Application.Models;
using AuthCenter.Contracts.Requests.Auth;
using AuthCenter.Contracts.Requests.Federation;
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
    private const string LinkProviderPurpose = "account.link-provider";
    private readonly IAuthService _auth;
    private readonly IAccountManagementService _accounts;
    private readonly IPasskeyService _passkeys;
    private readonly IRefreshTokenService _refreshTokens;
    private readonly IFederationService _federation;
    private readonly IApplicationService _applications;
    private readonly IMfaService _mfaService;
    private readonly ITokenService _tokens;
    private readonly IReauthenticationService _reauthentication;
    private readonly MfaSettings _mfa;
    private readonly SingleSignOnSettings _sso;

    public UiSessionController(
        IAuthService auth,
        IAccountManagementService accounts,
        IPasskeyService passkeys,
        IRefreshTokenService refreshTokens,
        IFederationService federation,
        IApplicationService applications,
        IMfaService mfaService,
        ITokenService tokens,
        IReauthenticationService reauthentication,
        IOptions<MfaSettings> mfa,
        IOptions<SingleSignOnSettings> sso)
    {
        _auth = auth;
        _accounts = accounts;
        _passkeys = passkeys;
        _refreshTokens = refreshTokens;
        _federation = federation;
        _applications = applications;
        _mfaService = mfaService;
        _tokens = tokens;
        _reauthentication = reauthentication;
        _mfa = mfa.Value;
        _sso = sso.Value;
    }

    /// <summary>Sign-in methods of an application for a direct (non-OAuth) visit to the hosted login.</summary>
    [AllowAnonymous]
    [HttpGet("login-options")]
    public async Task<IActionResult> LoginOptions([FromQuery] string applicationCode, CancellationToken ct)
    {
        var options = string.IsNullOrWhiteSpace(applicationCode) || applicationCode.Length > 50
            ? null
            : await _applications.GetLoginOptionsAsync(applicationCode.Trim(), ct);
        return options is null
            ? NotFound(ApiResponse<object>.Fail("APP_NOT_FOUND", "Application not found or inactive."))
            : Ok(ApiResponse<object>.Ok(options));
    }

    /// <summary>Emails a one-time code for the pending second factor of a user whose factor is email.</summary>
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingExtensions.SendMfaEmailOtp)]
    [HttpPost("mfa/email-otp")]
    public async Task<IActionResult> SendMfaEmailOtp([FromBody] SendMfaEmailOtpRequest request, CancellationToken ct)
    {
        var result = await _auth.SendMfaEmailOtpAsync(request, ct);
        return result.IsSuccess ? Ok(ApiResponse.Ok("A sign-in code was sent to your email address.")) : BadRequest(ApiResponse.Fail(result.ErrorCode, result.Message));
    }

    /// <summary>Starts the authenticator-app enrollment the application requires before signing in.</summary>
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingExtensions.Login)]
    [HttpPost("mfa/enrollment/start")]
    public async Task<IActionResult> StartTotpEnrollment([FromBody] TotpEnrollmentRequest request, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await _auth.BeginTotpEnrollmentAsync(request.EnrollmentToken, ct);
        return result.IsSuccess ? Ok(ApiResponse<object>.Ok(result.Data!)) : BadRequest(ApiResponse<object>.Fail(result.ErrorCode, result.Message));
    }

    /// <summary>Verifies the first code of the new authenticator and signs in; returns the backup codes once.</summary>
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingExtensions.MfaVerify)]
    [HttpPost("mfa/enrollment/complete")]
    public async Task<IActionResult> CompleteTotpEnrollment([FromBody] TotpEnrollmentRequest request, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await _auth.CompleteTotpEnrollmentAsync(request.EnrollmentToken, request.TotpCode ?? string.Empty, IpAddress(), Request.Headers.UserAgent.ToString(), ct);
        if (!result.IsSuccess)
            return BadRequest(ApiResponse<object>.Fail(result.ErrorCode, result.Message));
        await CreateSessionAsync(result.Data!.Session);
        return Ok(ApiResponse<object>.Ok(new
        {
            user = result.Data.Session.User,
            csrfToken = UiCsrfMiddleware.IssueToken(Response),
            backupCodes = result.Data.BackupCodes
        }));
    }

    /// <summary>WebAuthn registration options for the passkey the application requires.</summary>
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingExtensions.Login)]
    [HttpPost("passkey/enrollment/options")]
    public async Task<IActionResult> PasskeyEnrollmentOptions([FromBody] PasskeyEnrollmentRequest request, CancellationToken ct)
    {
        if (await _auth.GetPasskeyEnrollmentUserAsync(request.EnrollmentToken, ct) is not { } userId)
            return BadRequest(ApiResponse<object>.Fail("INVALID_ENROLLMENT", "The enrollment expired or was already used. Sign in again."));
        var result = await _passkeys.GetRegistrationOptionsAsync(userId, ct);
        return result.IsSuccess ? Ok(ApiResponse<object>.Ok(result.Data!)) : BadRequest(ApiResponse<object>.Fail(result.ErrorCode, result.Message));
    }

    /// <summary>Registers the passkey; the user then signs in with it.</summary>
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingExtensions.Login)]
    [HttpPost("passkey/enrollment/complete")]
    public async Task<IActionResult> CompletePasskeyEnrollment([FromBody] PasskeyEnrollmentRequest request, CancellationToken ct)
    {
        if (await _auth.GetPasskeyEnrollmentUserAsync(request.EnrollmentToken, ct) is not { } userId)
            return BadRequest(ApiResponse<object>.Fail("INVALID_ENROLLMENT", "The enrollment expired or was already used. Sign in again."));
        var result = await _passkeys.RegisterAsync(userId, new RegisterPasskeyRequest
        {
            CredentialJson = request.CredentialJson ?? string.Empty,
            Name = string.IsNullOrWhiteSpace(request.Name) ? "Passkey" : request.Name.Trim()
        }, IpAddress(), Request.Headers.UserAgent.ToString(), ct);
        if (!result.IsSuccess)
            return BadRequest(ApiResponse<object>.Fail(result.ErrorCode, result.Message));
        await _auth.CompletePasskeyEnrollmentAsync(request.EnrollmentToken, IpAddress(), Request.Headers.UserAgent.ToString(), ct);
        return Ok(ApiResponse<object>.Ok(new { registered = true }));
    }

    /// <summary>Signs the browser in with the link AuthCenter emailed (hosted <c>/magic-link</c> page).</summary>
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingExtensions.MagicLinkVerify)]
    [HttpPost("magic-link")]
    public async Task<IActionResult> MagicLink([FromBody] HostedMagicLinkRequest request, CancellationToken ct)
    {
        var token = request.Token ?? string.Empty;
        var verify = new VerifyMagicLinkRequest { Token = token, ApplicationCode = _tokens.ValidateMagicLinkToken(token)?.ApplicationCode ?? string.Empty };
        return await CompleteInteractiveStepAsync(await _auth.VerifyMagicLinkAsync(verify, IpAddress(), Request.Headers.UserAgent.ToString(), ct));
    }

    /// <summary>
    /// Home realm discovery: which upstream provider, if any, signs this email in to the
    /// application of the interaction (or of <c>applicationCode</c> for a direct sign-in).
    /// </summary>
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingExtensions.FederationDiscover)]
    [HttpPost("federation/discover")]
    public async Task<IActionResult> DiscoverFederation([FromBody] FederationDiscoveryRequest request, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await _federation.DiscoverAsync(request, FederationCaller(BrowserBinding.Read(HttpContext)), ct);
        return result.IsSuccess ? Ok(ApiResponse<object>.Ok(result.Data!)) : BadRequest(ApiResponse<object>.Fail(result.ErrorCode, result.Message));
    }

    /// <summary>
    /// Returns the upstream URL to navigate to; the sign-in is bound to this browser. With
    /// <c>link</c> the portal adds the provider to the signed-in account instead, which needs a
    /// recent reauthentication (<c>account.link-provider</c>).
    /// </summary>
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingExtensions.FederationStart)]
    [HttpPost("federation/start")]
    public async Task<IActionResult> StartFederation([FromBody] StartFederationRequest request, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        var caller = FederationCaller(BrowserBinding.Ensure(HttpContext));
        if (request.Link && caller.SignedInUserId is { } userId &&
            !await _reauthentication.ConsumeProofAsync(userId, LinkProviderPurpose, Request.Headers["X-AuthCenter-Reauthentication"].ToString(), ct))
            return StatusCode(StatusCodes.Status403Forbidden, ApiResponse<object>.Fail("REAUTHENTICATION_REQUIRED", $"A recent single-use reauthentication proof for {LinkProviderPurpose} is required."));
        var result = await _federation.StartAsync(request, caller, ct);
        return result.IsSuccess ? Ok(ApiResponse<object>.Ok(result.Data!)) : BadRequest(ApiResponse<object>.Fail(result.ErrorCode, result.Message));
    }

    /// <summary>Enterprise providers of the user's applications, and whether each is already linked.</summary>
    [Authorize]
    [HttpGet("federation/linkable")]
    public async Task<IActionResult> LinkableProviders(CancellationToken ct) =>
        SessionClaims.UserId(User) is { } userId
            ? Ok(ApiResponse<object>.Ok(await _federation.GetLinkableProvidersAsync(userId, ct)))
            : Unauthorized();

    /// <summary>Redeems, in the portal that started it, the result of linking an enterprise provider.</summary>
    [Authorize]
    [EnableRateLimiting(RateLimitingExtensions.FederationComplete)]
    [HttpPost("federation/link")]
    public async Task<IActionResult> CompleteFederationLink([FromBody] FederationResultRequest request, CancellationToken ct)
    {
        var result = await _federation.RedeemLinkAsync(request.Handle, FederationCaller(BrowserBinding.Read(HttpContext)), ct);
        return result.IsSuccess ? Ok(ApiResponse<object>.Ok(result.Data!)) : BadRequest(ApiResponse<object>.Fail(result.ErrorCode, result.Message));
    }

    /// <summary>
    /// Redeems the result the upstream callback left for this browser. The application's access
    /// policy and MFA gate run here, so the answer is a session, a pending second factor or an error.
    /// </summary>
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingExtensions.FederationComplete)]
    [HttpPost("federation/complete")]
    public async Task<IActionResult> CompleteFederation([FromBody] FederationResultRequest request, CancellationToken ct)
    {
        var result = await _federation.RedeemResultAsync(request.Handle, FederationCaller(BrowserBinding.Read(HttpContext)), ct);
        return await CompleteInteractiveStepAsync(result);
    }

    private FederationCaller FederationCaller(string? browserBinding) => new(
        browserBinding,
        string.Equals(User.Identity?.AuthenticationType, AuthenticationSchemes.UiCookie, StringComparison.Ordinal) ? SessionClaims.UserId(User) : null,
        IpAddress(),
        Request.Headers.UserAgent.ToString());

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
        return await CompleteInteractiveStepAsync(await _passkeys.LoginAsync(request, IpAddress(), Request.Headers.UserAgent.ToString(), ct));
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
            return Ok(ApiResponse<object>.Ok(new { requiresMfa = true, mfaPendingToken = result.Message, mfaMethod = await MfaMethodAsync(result.Message), expiresIn = _mfa.MfaTokenExpirySeconds }));
        if (!result.IsSuccess && result.ErrorCode == "MFA_SETUP_REQUIRED")
            return Ok(ApiResponse<object>.Ok(new { requiresMfaEnrollment = true, enrollmentToken = result.Message, expiresIn = _mfa.MfaTokenExpirySeconds * 2 }));
        if (!result.IsSuccess && result.ErrorCode == "PASSKEY_ENROLLMENT_REQUIRED")
            return Ok(ApiResponse<object>.Ok(new { requiresPasskeyEnrollment = true, enrollmentToken = result.Message, expiresIn = _mfa.MfaTokenExpirySeconds * 2 }));
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
            // Lets the portal mark this browser's own session in the session list.
            sessionId = SessionClaims.SessionId(User),
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

    // The hosted login shows "send me a code" only to users whose second factor is email.
    private async Task<string> MfaMethodAsync(string? pendingToken)
    {
        var pending = string.IsNullOrWhiteSpace(pendingToken) ? null : _tokens.ValidateMfaPendingToken(pendingToken);
        if (pending is null)
            return "totp";
        var status = await _mfaService.GetStatusAsync(pending.UserId, HttpContext.RequestAborted);
        return status.Method == nameof(AuthCenter.Domain.Enums.MfaMethod.EmailOtp) ? "email" : "totp";
    }
    private string? IpAddress() => HttpContext.Connection.RemoteIpAddress?.ToString();
}
