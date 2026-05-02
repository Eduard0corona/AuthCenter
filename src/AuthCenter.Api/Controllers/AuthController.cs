using AuthCenter.Api.Extensions;
using AuthCenter.Application.Interfaces;
using AuthCenter.Contracts.Requests.Auth;
using AuthCenter.Contracts.Responses;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AuthCenter.Api.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly IAuthService _authService;
    private readonly ICurrentUserService _currentUserService;

    public AuthController(IAuthService authService, ICurrentUserService currentUserService)
    {
        _authService = authService;
        _currentUserService = currentUserService;
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
        if (!result.IsSuccess)
            return Unauthorized(ApiResponse<object>.Fail(result.ErrorCode, result.Message));
        return Ok(ApiResponse<object>.Ok(result.Data!));
    }

    [HttpPost("google")]
    public async Task<IActionResult> GoogleLogin([FromBody] GoogleLoginRequest request, CancellationToken ct)
    {
        var result = await _authService.GoogleLoginAsync(request, GetIpAddress(), GetUserAgent(), ct);
        if (!result.IsSuccess)
            return Unauthorized(ApiResponse<object>.Fail(result.ErrorCode, result.Message));
        return Ok(ApiResponse<object>.Ok(result.Data!));
    }

    [EnableRateLimiting(RateLimitingExtensions.Refresh)]
    [HttpPost("refresh-token")]
    public async Task<IActionResult> RefreshToken([FromBody] RefreshTokenRequest request, CancellationToken ct)
    {
        var result = await _authService.RefreshTokenAsync(request.RefreshToken, GetIpAddress(), GetUserAgent(), ct);
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

    [EnableRateLimiting(RateLimitingExtensions.ForgotPassword)]
    [HttpPost("forgot-password")]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequest request, CancellationToken ct)
    {
        await _authService.ForgotPasswordAsync(request, GetIpAddress(), ct);
        // Always 200 — do not reveal whether the email exists
        return Ok(ApiResponse.Ok("If this email exists, a reset link has been sent."));
    }

    [HttpPost("reset-password")]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest request, CancellationToken ct)
    {
        var result = await _authService.ResetPasswordAsync(request, GetIpAddress(), ct);
        if (!result.IsSuccess)
            return BadRequest(ApiResponse.Fail(result.ErrorCode, result.Message));
        return Ok(ApiResponse.Ok("Password has been reset successfully."));
    }

    private string? GetIpAddress() =>
        HttpContext.Connection.RemoteIpAddress?.ToString();

    private string? GetUserAgent() =>
        Request.Headers.UserAgent.ToString();
}
