using AuthCenter.Api.Extensions;
using AuthCenter.Application.Interfaces;
using AuthCenter.Contracts.Requests.Auth;
using AuthCenter.Contracts.Responses;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AuthCenter.Api.Controllers;

[ApiController]
[Route("api/auth/passkeys")]
public sealed class PasskeysController : ControllerBase
{
    private readonly IPasskeyService _passkeys;
    private readonly IReauthenticationService _reauthentication;
    private readonly ICurrentUserService _currentUser;

    public PasskeysController(IPasskeyService passkeys, IReauthenticationService reauthentication, ICurrentUserService currentUser)
    {
        _passkeys = passkeys;
        _reauthentication = reauthentication;
        _currentUser = currentUser;
    }

    [Authorize]
    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken ct)
    {
        var userId = _currentUser.UserId;
        return userId is null ? Unauthorized() : Ok(ApiResponse<object>.Ok(await _passkeys.GetAllAsync(userId.Value, ct)));
    }

    [Authorize]
    [HttpPost("registration/options")]
    public async Task<IActionResult> RegistrationOptions(CancellationToken ct)
    {
        var userId = _currentUser.UserId;
        if (userId is null) return Unauthorized();
        var result = await _passkeys.GetRegistrationOptionsAsync(userId.Value, ct);
        return result.IsSuccess ? Ok(ApiResponse<object>.Ok(result.Data!)) : BadRequest(ApiResponse<object>.Fail(result.ErrorCode, result.Message));
    }

    [Authorize]
    [HttpPost("registration/complete")]
    public async Task<IActionResult> CompleteRegistration([FromBody] RegisterPasskeyRequest request, CancellationToken ct)
    {
        var userId = _currentUser.UserId;
        if (userId is null) return Unauthorized();
        if (!await HasProofAsync(userId.Value, "factor.enroll", ct))
            return StatusCode(StatusCodes.Status403Forbidden, ApiResponse.Fail("REAUTHENTICATION_REQUIRED", "A recent single-use reauthentication proof for factor.enroll is required."));
        var result = await _passkeys.RegisterAsync(userId.Value, request, GetIpAddress(), GetUserAgent(), ct);
        return result.IsSuccess ? Ok(ApiResponse<object>.Ok(result.Data!)) : BadRequest(ApiResponse<object>.Fail(result.ErrorCode, result.Message));
    }

    [Authorize]
    [HttpPut("{credentialId}")]
    public async Task<IActionResult> Rename(string credentialId, [FromBody] RenamePasskeyRequest request, CancellationToken ct)
    {
        var userId = _currentUser.UserId;
        if (userId is null) return Unauthorized();
        if (!await HasProofAsync(userId.Value, "passkey.manage", ct))
            return StatusCode(StatusCodes.Status403Forbidden, ApiResponse.Fail("REAUTHENTICATION_REQUIRED", "A recent single-use reauthentication proof for passkey.manage is required."));
        var result = await _passkeys.RenameAsync(userId.Value, credentialId, request, ct);
        return result.IsSuccess ? Ok(ApiResponse.Ok()) : BadRequest(ApiResponse.Fail(result.ErrorCode, result.Message));
    }

    [Authorize]
    [HttpDelete("{credentialId}")]
    public async Task<IActionResult> Remove(string credentialId, CancellationToken ct)
    {
        var userId = _currentUser.UserId;
        if (userId is null) return Unauthorized();
        if (!await HasProofAsync(userId.Value, "passkey.manage", ct))
            return StatusCode(StatusCodes.Status403Forbidden, ApiResponse.Fail("REAUTHENTICATION_REQUIRED", "A recent single-use reauthentication proof for passkey.manage is required."));
        var result = await _passkeys.RemoveAsync(userId.Value, credentialId, ct);
        return result.IsSuccess ? Ok(ApiResponse.Ok()) : BadRequest(ApiResponse.Fail(result.ErrorCode, result.Message));
    }

    [EnableRateLimiting(RateLimitingExtensions.Login)]
    [HttpPost("login/options")]
    public async Task<IActionResult> LoginOptions([FromBody] BeginPasskeyLoginRequest request, CancellationToken ct)
    {
        var result = await _passkeys.GetLoginOptionsAsync(request, ct);
        return result.IsSuccess ? Ok(ApiResponse<object>.Ok(result.Data!)) : BadRequest(ApiResponse<object>.Fail(result.ErrorCode, result.Message));
    }

    [EnableRateLimiting(RateLimitingExtensions.Login)]
    [HttpPost("login/complete")]
    public async Task<IActionResult> CompleteLogin([FromBody] CompletePasskeyLoginRequest request, CancellationToken ct)
    {
        var result = await _passkeys.LoginAsync(request, GetIpAddress(), GetUserAgent(), ct);
        return result.IsSuccess ? Ok(ApiResponse<object>.Ok(result.Data!)) : Unauthorized(ApiResponse<object>.Fail(result.ErrorCode, result.Message));
    }

    [Authorize]
    [EnableRateLimiting(RateLimitingExtensions.Login)]
    [HttpPost("step-up/options")]
    public async Task<IActionResult> StepUpOptions([FromBody] BeginPasskeyStepUpRequest request, CancellationToken ct)
    {
        var userId = _currentUser.UserId;
        if (userId is null) return Unauthorized();
        var result = await _reauthentication.GetPasskeyOptionsAsync(userId.Value, request, ct);
        return result.IsSuccess
            ? Ok(ApiResponse<object>.Ok(result.Data!))
            : BadRequest(ApiResponse<object>.Fail(result.ErrorCode, result.Message));
    }

    [Authorize]
    [EnableRateLimiting(RateLimitingExtensions.Login)]
    [HttpPost("step-up/complete")]
    public async Task<IActionResult> CompleteStepUp([FromBody] CompletePasskeyStepUpRequest request, CancellationToken ct)
    {
        var userId = _currentUser.UserId;
        if (userId is null) return Unauthorized();
        var result = await _reauthentication.VerifyPasskeyAsync(userId.Value, request, GetIpAddress(), GetUserAgent(), ct);
        return result.IsSuccess
            ? Ok(ApiResponse<object>.Ok(result.Data!))
            : Unauthorized(ApiResponse<object>.Fail(result.ErrorCode, result.Message));
    }

    private string? GetIpAddress() => HttpContext.Connection.RemoteIpAddress?.ToString();
    private string? GetUserAgent() => Request.Headers.UserAgent.ToString();
    private Task<bool> HasProofAsync(Guid userId, string purpose, CancellationToken ct) =>
        _reauthentication.ConsumeProofAsync(userId, purpose, Request.Headers["X-AuthCenter-Reauthentication"].ToString(), ct);
}
