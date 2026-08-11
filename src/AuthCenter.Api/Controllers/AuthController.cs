using AuthCenter.Api.Extensions;
using AuthCenter.Application.Interfaces;
using AuthCenter.Contracts.Requests.Auth;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Responses.Auth;
using AuthCenter.Infrastructure.Settings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace AuthCenter.Api.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly IAuthService _authService;
    private readonly ICurrentUserService _currentUserService;
    private readonly IAccountManagementService _accountManagementService;
    private readonly IMfaService _mfaService;
    private readonly IExternalIdentityLinkService _externalIdentityLinkService;
    private readonly IReauthenticationService _reauthenticationService;
    private readonly MfaSettings _mfaSettings;

    public AuthController(
        IAuthService authService,
        ICurrentUserService currentUserService,
        IAccountManagementService accountManagementService,
        IMfaService mfaService,
        IExternalIdentityLinkService externalIdentityLinkService,
        IReauthenticationService reauthenticationService,
        IOptions<MfaSettings> mfaSettings)
    {
        _authService = authService;
        _currentUserService = currentUserService;
        _accountManagementService = accountManagementService;
        _mfaService = mfaService;
        _externalIdentityLinkService = externalIdentityLinkService;
        _reauthenticationService = reauthenticationService;
        _mfaSettings = mfaSettings.Value;
    }

    [EnableRateLimiting(RateLimitingExtensions.Register)]
    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request, CancellationToken ct)
    {
        var result = await _authService.RegisterAsync(request, GetIpAddress(), GetUserAgent(), ct);
        if (!result.IsSuccess)
            return BadRequest(ApiResponse<object>.Fail(result.ErrorCode, result.Message, result.Details));
        return Ok(ApiResponse<object>.Ok(result.Data!));
    }

    [EnableRateLimiting(RateLimitingExtensions.Login)]
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken ct)
    {
        var result = await _authService.LoginAsync(request, GetIpAddress(), GetUserAgent(), ct);
        if (!result.IsSuccess && result.ErrorCode == "MFA_REQUIRED")
            return Ok(ApiResponse<object>.Ok(new MfaPendingResponse
            {
                MfaPendingToken = result.Message,
                ExpiresIn = _mfaSettings.MfaTokenExpirySeconds
            }));
        if (!result.IsSuccess && result.ErrorCode == "PASSWORD_CHANGE_REQUIRED")
            return Ok(ApiResponse<object>.Ok(new ForcedChangePendingResponse
            {
                ForcedChangePendingToken = result.Message,
                ExpiresIn = _mfaSettings.MfaTokenExpirySeconds
            }));

        if (!result.IsSuccess)
            return Unauthorized(ApiResponse<object>.Fail(result.ErrorCode, result.Message));
        return Ok(ApiResponse<object>.Ok(result.Data!));
    }

    [EnableRateLimiting(RateLimitingExtensions.Google)]
    [HttpPost("google")]
    public async Task<IActionResult> GoogleLogin([FromBody] GoogleLoginRequest request, CancellationToken ct)
    {
        var result = await _authService.GoogleLoginAsync(request, GetIpAddress(), GetUserAgent(), ct);
        if (!result.IsSuccess && result.ErrorCode == "MFA_REQUIRED")
            return Ok(ApiResponse<object>.Ok(new MfaPendingResponse
            {
                MfaPendingToken = result.Message,
                ExpiresIn = _mfaSettings.MfaTokenExpirySeconds
            }));

        if (!result.IsSuccess)
            return Unauthorized(ApiResponse<object>.Fail(result.ErrorCode, result.Message));
        return Ok(ApiResponse<object>.Ok(result.Data!));
    }

    [EnableRateLimiting(RateLimitingExtensions.Microsoft)]
    [HttpPost("microsoft")]
    public async Task<IActionResult> MicrosoftLogin([FromBody] MicrosoftLoginRequest request, CancellationToken ct)
    {
        var result = await _authService.MicrosoftLoginAsync(request, GetIpAddress(), GetUserAgent(), ct);
        if (!result.IsSuccess && result.ErrorCode == "MFA_REQUIRED")
            return Ok(ApiResponse<object>.Ok(new MfaPendingResponse
            {
                MfaPendingToken = result.Message,
                ExpiresIn = _mfaSettings.MfaTokenExpirySeconds
            }));

        if (!result.IsSuccess)
            return Unauthorized(ApiResponse<object>.Fail(result.ErrorCode, result.Message));

        return Ok(ApiResponse<object>.Ok(result.Data!));
    }

    [EnableRateLimiting(RateLimitingExtensions.GitHub)]
    [HttpPost("github")]
    public async Task<IActionResult> GitHubLogin([FromBody] GitHubLoginRequest request, CancellationToken ct)
    {
        var result = await _authService.GitHubLoginAsync(request, GetIpAddress(), GetUserAgent(), ct);
        if (!result.IsSuccess && result.ErrorCode == "MFA_REQUIRED")
            return Ok(ApiResponse<object>.Ok(new MfaPendingResponse
            {
                MfaPendingToken = result.Message,
                ExpiresIn = _mfaSettings.MfaTokenExpirySeconds
            }));

        if (!result.IsSuccess)
            return Unauthorized(ApiResponse<object>.Fail(result.ErrorCode, result.Message));

        return Ok(ApiResponse<object>.Ok(result.Data!));
    }

    [EnableRateLimiting(RateLimitingExtensions.Apple)]
    [HttpPost("apple")]
    public async Task<IActionResult> AppleLogin([FromBody] AppleLoginRequest request, CancellationToken ct)
    {
        var result = await _authService.AppleLoginAsync(request, GetIpAddress(), GetUserAgent(), ct);
        if (!result.IsSuccess && result.ErrorCode == "MFA_REQUIRED")
            return Ok(ApiResponse<object>.Ok(new MfaPendingResponse
            {
                MfaPendingToken = result.Message,
                ExpiresIn = _mfaSettings.MfaTokenExpirySeconds
            }));

        if (!result.IsSuccess)
            return Unauthorized(ApiResponse<object>.Fail(result.ErrorCode, result.Message));

        return Ok(ApiResponse<object>.Ok(result.Data!));
    }

    [EnableRateLimiting(RateLimitingExtensions.MagicLinkRequest)]
    [HttpPost("magic-link/request")]
    public async Task<IActionResult> RequestMagicLink([FromBody] MagicLinkRequest request, CancellationToken ct)
    {
        await _authService.SendMagicLinkAsync(request, GetIpAddress(), ct);
        return Ok(ApiResponse.Ok("If this email has an account, a sign-in link has been sent."));
    }

    [EnableRateLimiting(RateLimitingExtensions.MagicLinkVerify)]
    [HttpPost("magic-link/verify")]
    public async Task<IActionResult> VerifyMagicLink([FromBody] VerifyMagicLinkRequest request, CancellationToken ct)
    {
        var result = await _authService.VerifyMagicLinkAsync(request, GetIpAddress(), GetUserAgent(), ct);
        if (!result.IsSuccess && result.ErrorCode == "MFA_REQUIRED")
            return Ok(ApiResponse<object>.Ok(new MfaPendingResponse
            {
                MfaPendingToken = result.Message,
                ExpiresIn = _mfaSettings.MfaTokenExpirySeconds
            }));

        if (!result.IsSuccess)
            return Unauthorized(ApiResponse<object>.Fail(result.ErrorCode, result.Message));

        return Ok(ApiResponse<object>.Ok(result.Data!));
    }

    [EnableRateLimiting(RateLimitingExtensions.MfaVerify)]
    [HttpPost("mfa/verify")]
    public async Task<IActionResult> VerifyMfa([FromBody] VerifyMfaRequest request, CancellationToken ct)
    {
        var result = await _authService.VerifyMfaAsync(request, GetIpAddress(), GetUserAgent(), ct);
        if (!result.IsSuccess)
            return result.ErrorCode is "INVALID_MFA_CODE" or "INVALID_MFA_TOKEN" or "TOKEN_ALREADY_USED"
                ? Unauthorized(ApiResponse<object>.Fail(result.ErrorCode, result.Message))
                : BadRequest(ApiResponse<object>.Fail(result.ErrorCode, result.Message));

        return Ok(ApiResponse<object>.Ok(result.Data!));
    }

    [EnableRateLimiting(RateLimitingExtensions.SendMfaEmailOtp)]
    [HttpPost("mfa/email-otp/send")]
    public async Task<IActionResult> SendMfaEmailOtp([FromBody] SendMfaEmailOtpRequest request, CancellationToken ct)
    {
        var result = await _authService.SendMfaEmailOtpAsync(request, ct);
        if (!result.IsSuccess)
            return BadRequest(ApiResponse.Fail(result.ErrorCode, result.Message));

        return Ok(ApiResponse.Ok("A sign-in code was sent to your email address."));
    }

    [EnableRateLimiting(RateLimitingExtensions.ForcedChangePassword)]
    [HttpPost("forced-change-password")]
    public async Task<IActionResult> ForcedChangePassword([FromBody] ForcedChangePasswordRequest request, CancellationToken ct)
    {
        var result = await _authService.ForcedChangePasswordAsync(request, GetIpAddress(), GetUserAgent(), ct);
        if (!result.IsSuccess)
            return result.ErrorCode is "INVALID_FORCED_CHANGE_TOKEN" or "TOKEN_ALREADY_USED"
                ? Unauthorized(ApiResponse<object>.Fail(result.ErrorCode, result.Message))
                : BadRequest(ApiResponse<object>.Fail(result.ErrorCode, result.Message, result.Details));

        return Ok(ApiResponse<object>.Ok(result.Data!));
    }

    [EnableRateLimiting(RateLimitingExtensions.Refresh)]
    [HttpPost("refresh-token")]
    public async Task<IActionResult> RefreshToken([FromBody] RefreshTokenRequest request, CancellationToken ct)
    {
        var result = await _authService.RefreshTokenAsync(request.RefreshToken, request.ApplicationCode, GetIpAddress(), GetUserAgent(), ct);
        if (!result.IsSuccess)
            return Unauthorized(ApiResponse<object>.Fail(result.ErrorCode, result.Message));
        return Ok(ApiResponse<object>.Ok(result.Data!));
    }

    [Authorize]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout([FromBody] RevokeTokenRequest? request, CancellationToken ct)
    {
        var userId = _currentUserService.UserId;
        if (userId is null) return Unauthorized();

        var result = await _authService.LogoutAsync(userId.Value, request?.RefreshToken, ct);
        if (!result.IsSuccess)
            return BadRequest(ApiResponse.Fail(result.ErrorCode, result.Message));
        return Ok(ApiResponse.Ok());
    }

    [Authorize]
    [HttpPost("revoke-token")]
    public async Task<IActionResult> RevokeToken([FromBody] RevokeTokenRequest request, CancellationToken ct)
    {
        var userId = _currentUserService.UserId;
        if (userId is null) return Unauthorized();

        var result = await _authService.RevokeTokenAsync(request.RefreshToken, userId.Value, ct);
        if (!result.IsSuccess)
            return BadRequest(ApiResponse.Fail(result.ErrorCode, result.Message));
        return Ok(ApiResponse.Ok());
    }

    [Authorize]
    [HttpGet("me")]
    public async Task<IActionResult> Me([FromServices] IUserAccessService userAccessService, [FromServices] IRoleService roleService, CancellationToken ct)
    {
        var userId = _currentUserService.UserId;
        if (userId is null) return Unauthorized();

        var user = await userAccessService.GetUserByIdAsync(userId.Value, ct);
        if (user is null) return NotFound(ApiResponse<object>.Fail("USER_NOT_FOUND", "User not found."));
        return Ok(ApiResponse<object>.Ok(user));
    }

    [Authorize]
    [HttpGet("mfa/status")]
    public async Task<IActionResult> GetMfaStatus(CancellationToken ct)
    {
        var userId = _currentUserService.UserId;
        if (userId is null) return Unauthorized();

        var status = await _mfaService.GetStatusAsync(userId.Value, ct);
        return Ok(ApiResponse<object>.Ok(status));
    }

    [Authorize]
    [HttpPost("mfa/setup")]
    public async Task<IActionResult> SetupMfa(CancellationToken ct)
    {
        var userId = _currentUserService.UserId;
        if (userId is null) return Unauthorized();
        if (!await HasProofAsync(userId.Value, "factor.enroll", ct)) return ReauthenticationRequired("factor.enroll");

        var result = await _mfaService.SetupTotpAsync(userId.Value, ct);
        if (!result.IsSuccess)
            return BadRequest(ApiResponse<object>.Fail(result.ErrorCode, result.Message));

        return Ok(ApiResponse<object>.Ok(result.Data!));
    }

    [Authorize]
    [HttpPost("mfa/enable")]
    public async Task<IActionResult> EnableMfa([FromBody] EnableMfaRequest request, CancellationToken ct)
    {
        var userId = _currentUserService.UserId;
        if (userId is null) return Unauthorized();

        var result = await _mfaService.EnableTotpAsync(userId.Value, request, ct);
        if (!result.IsSuccess)
            return BadRequest(ApiResponse<object>.Fail(result.ErrorCode, result.Message));

        return Ok(ApiResponse<object>.Ok(result.Data!));
    }

    [Authorize]
    [HttpDelete("mfa")]
    public async Task<IActionResult> DisableMfa([FromBody] DisableMfaRequest request, CancellationToken ct)
    {
        var userId = _currentUserService.UserId;
        if (userId is null) return Unauthorized();

        var result = await _mfaService.DisableMfaAsync(userId.Value, request, ct);
        if (!result.IsSuccess)
            return BadRequest(ApiResponse.Fail(result.ErrorCode, result.Message));

        return Ok(ApiResponse.Ok("MFA disabled successfully."));
    }

    [Authorize]
    [HttpPost("mfa/backup-codes")]
    public async Task<IActionResult> RegenerateBackupCodes([FromBody] RegenerateBackupCodesRequest request, CancellationToken ct)
    {
        var userId = _currentUserService.UserId;
        if (userId is null) return Unauthorized();

        var result = await _mfaService.RegenerateBackupCodesAsync(userId.Value, request.TotpCode, ct);
        if (!result.IsSuccess)
            return BadRequest(ApiResponse<object>.Fail(result.ErrorCode, result.Message));

        return Ok(ApiResponse<object>.Ok(result.Data!));
    }

    [Authorize]
    [HttpPost("mfa/email-otp/setup")]
    public async Task<IActionResult> SetupEmailMfa(CancellationToken ct)
    {
        var userId = _currentUserService.UserId;
        if (userId is null) return Unauthorized();
        if (!await HasProofAsync(userId.Value, "factor.enroll", ct)) return ReauthenticationRequired("factor.enroll");

        var result = await _mfaService.SetupEmailOtpAsync(userId.Value, ct);
        if (!result.IsSuccess)
            return BadRequest(ApiResponse.Fail(result.ErrorCode, result.Message));

        return Ok(ApiResponse.Ok("A verification code was sent to your email address."));
    }

    [Authorize]
    [EnableRateLimiting(RateLimitingExtensions.MfaEmailOtpEnable)]
    [HttpPost("mfa/email-otp/enable")]
    public async Task<IActionResult> EnableEmailMfa([FromBody] EnableEmailMfaRequest request, CancellationToken ct)
    {
        var userId = _currentUserService.UserId;
        if (userId is null) return Unauthorized();

        var result = await _mfaService.EnableEmailOtpAsync(userId.Value, request, ct);
        if (!result.IsSuccess)
            return BadRequest(ApiResponse.Fail(result.ErrorCode, result.Message));

        return Ok(ApiResponse.Ok("Email OTP MFA enabled successfully."));
    }

    [Authorize]
    [HttpGet("trusted-devices")]
    public async Task<IActionResult> GetTrustedDevices(CancellationToken ct)
    {
        var userId = _currentUserService.UserId;
        if (userId is null) return Unauthorized();

        var devices = await _accountManagementService.GetTrustedDevicesAsync(userId.Value, ct);
        return Ok(ApiResponse<object>.Ok(devices));
    }

    [Authorize]
    [HttpDelete("trusted-devices/{deviceId:guid}")]
    public async Task<IActionResult> RevokeTrustedDevice(Guid deviceId, CancellationToken ct)
    {
        var userId = _currentUserService.UserId;
        if (userId is null) return Unauthorized();

        var result = await _accountManagementService.RevokeTrustedDeviceAsync(userId.Value, deviceId, ct);
        if (!result.IsSuccess)
            return BadRequest(ApiResponse.Fail(result.ErrorCode, result.Message));

        return Ok(ApiResponse.Ok("Trusted device revoked."));
    }

    [Authorize]
    [HttpDelete("trusted-devices")]
    public async Task<IActionResult> RevokeAllTrustedDevices(CancellationToken ct)
    {
        var userId = _currentUserService.UserId;
        if (userId is null) return Unauthorized();

        var result = await _accountManagementService.RevokeAllTrustedDevicesAsync(userId.Value, ct);
        if (!result.IsSuccess)
            return BadRequest(ApiResponse.Fail(result.ErrorCode, result.Message));

        return Ok(ApiResponse.Ok("Trusted devices revoked."));
    }

    [EnableRateLimiting(RateLimitingExtensions.ForgotPassword)]
    [HttpPost("forgot-password")]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequest request, CancellationToken ct)
    {
        await _authService.ForgotPasswordAsync(request, GetIpAddress(), ct);
        // Always 200 — do not reveal whether the email exists
        return Ok(ApiResponse.Ok("If this email exists, a reset link has been sent."));
    }

    [EnableRateLimiting(RateLimitingExtensions.ResetPassword)]
    [HttpPost("reset-password")]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest request, CancellationToken ct)
    {
        var result = await _authService.ResetPasswordAsync(request, GetIpAddress(), ct);
        if (!result.IsSuccess)
            return BadRequest(ApiResponse.Fail(result.ErrorCode, result.Message));
        return Ok(ApiResponse.Ok("Password has been reset successfully."));
    }

    [EnableRateLimiting(RateLimitingExtensions.ConfirmEmail)]
    [HttpPost("confirm-email")]
    public async Task<IActionResult> ConfirmEmail([FromBody] ConfirmEmailRequest request, CancellationToken ct)
    {
        var result = await _authService.ConfirmEmailAsync(request, GetIpAddress(), ct);
        if (!result.IsSuccess)
            return BadRequest(ApiResponse.Fail(result.ErrorCode, result.Message));
        return Ok(ApiResponse.Ok("Email has been confirmed successfully."));
    }

    [EnableRateLimiting(RateLimitingExtensions.ResendEmailConfirmation)]
    [HttpPost("resend-email-confirmation")]
    public async Task<IActionResult> ResendEmailConfirmation([FromBody] ResendEmailConfirmationRequest request, CancellationToken ct)
    {
        await _authService.ResendEmailConfirmationAsync(request, GetIpAddress(), ct);
        return Ok(ApiResponse.Ok("If this account requires confirmation, a confirmation email has been sent."));
    }

    // --- Account management ---

    [Authorize]
    [EnableRateLimiting(RateLimitingExtensions.ChangePassword)]
    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request, CancellationToken ct)
    {
        var userId = _currentUserService.UserId;
        if (userId is null) return Unauthorized();

        var result = await _accountManagementService.ChangePasswordAsync(userId.Value, request, ct);
        if (!result.IsSuccess)
            return BadRequest(ApiResponse.Fail(result.ErrorCode, result.Message));
        return Ok(ApiResponse.Ok("Password changed successfully."));
    }

    [Authorize]
    [HttpGet("sessions")]
    public async Task<IActionResult> GetSessions(CancellationToken ct)
    {
        var userId = _currentUserService.UserId;
        if (userId is null) return Unauthorized();

        var sessions = await _accountManagementService.GetActiveSessionsAsync(userId.Value, ct);
        return Ok(ApiResponse<object>.Ok(sessions));
    }

    [Authorize]
    [HttpDelete("sessions/{tokenId:guid}")]
    public async Task<IActionResult> RevokeSession(Guid tokenId, CancellationToken ct)
    {
        var userId = _currentUserService.UserId;
        if (userId is null) return Unauthorized();

        var result = await _accountManagementService.RevokeSessionAsync(userId.Value, tokenId, ct);
        if (!result.IsSuccess)
            return BadRequest(ApiResponse.Fail(result.ErrorCode, result.Message));
        return Ok(ApiResponse.Ok("Session revoked."));
    }

    [Authorize]
    [HttpDelete("sessions")]
    public async Task<IActionResult> RevokeAllSessions(CancellationToken ct)
    {
        var userId = _currentUserService.UserId;
        if (userId is null) return Unauthorized();
        if (!await HasProofAsync(userId.Value, "session.revoke-all", ct)) return ReauthenticationRequired("session.revoke-all");

        var result = await _accountManagementService.RevokeAllSessionsAsync(userId.Value, ct);
        if (!result.IsSuccess)
            return BadRequest(ApiResponse.Fail(result.ErrorCode, result.Message));
        return Ok(ApiResponse.Ok("All sessions revoked."));
    }

    [Authorize]
    [HttpPost("external-providers/link")]
    public async Task<IActionResult> LinkExternalProvider(
        [FromBody] LinkExternalProviderRequest request,
        CancellationToken ct)
    {
        var userId = _currentUserService.UserId;
        if (userId is null) return Unauthorized();

        var result = await _externalIdentityLinkService.LinkAsync(userId.Value, request, ct);
        return result.IsSuccess
            ? Ok(ApiResponse.Ok())
            : BadRequest(ApiResponse.Fail(result.ErrorCode, result.Message));
    }

    [Authorize]
    [HttpGet("external-providers")]
    public async Task<IActionResult> GetExternalProviders(CancellationToken ct)
    {
        var userId = _currentUserService.UserId;
        if (userId is null) return Unauthorized();

        var providers = await _accountManagementService.GetExternalProvidersAsync(userId.Value, ct);
        return Ok(ApiResponse<object>.Ok(providers));
    }

    [Authorize]
    [HttpDelete("external-providers/{providerId:guid}")]
    public async Task<IActionResult> UnlinkExternalProvider(Guid providerId, CancellationToken ct)
    {
        var userId = _currentUserService.UserId;
        if (userId is null) return Unauthorized();

        var result = await _accountManagementService.UnlinkExternalProviderAsync(userId.Value, providerId, ct);
        if (!result.IsSuccess)
            return BadRequest(ApiResponse.Fail(result.ErrorCode, result.Message));
        return Ok(ApiResponse.Ok("External provider unlinked."));
    }

    [Authorize]
    [EnableRateLimiting(RateLimitingExtensions.EmailChangeRequest)]
    [HttpPost("email-change/request")]
    public async Task<IActionResult> RequestEmailChange([FromBody] RequestEmailChangeRequest request, CancellationToken ct)
    {
        var userId = _currentUserService.UserId;
        if (userId is null) return Unauthorized();
        if (!await HasProofAsync(userId.Value, "account.change-email", ct)) return ReauthenticationRequired("account.change-email");

        var result = await _accountManagementService.RequestEmailChangeAsync(userId.Value, request, ct);
        if (!result.IsSuccess)
            return BadRequest(ApiResponse.Fail(result.ErrorCode, result.Message));
        return Ok(ApiResponse.Ok("A confirmation email has been sent to your new address."));
    }

    [HttpPost("email-change/confirm")]
    public async Task<IActionResult> ConfirmEmailChange([FromBody] ConfirmEmailChangeRequest request, CancellationToken ct)
    {
        var result = await _accountManagementService.ConfirmEmailChangeAsync(request, ct);
        if (!result.IsSuccess)
            return BadRequest(ApiResponse.Fail(result.ErrorCode, result.Message));
        return Ok(ApiResponse.Ok("Email changed successfully."));
    }

    [Authorize]
    [HttpDelete("account")]
    public async Task<IActionResult> DeleteAccount([FromBody] DeleteAccountRequest request, CancellationToken ct)
    {
        var userId = _currentUserService.UserId;
        if (userId is null) return Unauthorized();

        var result = await _accountManagementService.DeleteAccountAsync(userId.Value, request, ct);
        if (!result.IsSuccess)
            return BadRequest(ApiResponse.Fail(result.ErrorCode, result.Message));
        return Ok(ApiResponse.Ok("Account deleted successfully."));
    }

    private string? GetIpAddress() =>
        HttpContext.Connection.RemoteIpAddress?.ToString();

    private string? GetUserAgent() =>
        Request.Headers.UserAgent.ToString();

    private Task<bool> HasProofAsync(Guid userId, string purpose, CancellationToken ct) =>
        _reauthenticationService.ConsumeProofAsync(userId, purpose, Request.Headers["X-AuthCenter-Reauthentication"].ToString(), ct);

    private ObjectResult ReauthenticationRequired(string purpose) =>
        StatusCode(StatusCodes.Status403Forbidden, ApiResponse.Fail("REAUTHENTICATION_REQUIRED", $"A recent single-use reauthentication proof for {purpose} is required."));
}
