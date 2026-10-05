using AuthCenter.Api.Extensions;
using AuthCenter.Application.Interfaces;
using AuthCenter.Contracts.Requests.Auth;
using AuthCenter.Contracts.Responses;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AuthCenter.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/auth/reauth")]
public sealed class ReauthenticationController : ControllerBase
{
    private readonly IReauthenticationService _reauthentication;
    private readonly ICurrentUserService _currentUser;

    public ReauthenticationController(IReauthenticationService reauthentication, ICurrentUserService currentUser)
    {
        _reauthentication = reauthentication;
        _currentUser = currentUser;
    }

    [EnableRateLimiting(RateLimitingExtensions.Login)]
    [HttpPost("password")]
    public async Task<IActionResult> Password([FromBody] PasswordReauthenticationRequest request, CancellationToken ct)
    {
        var userId = _currentUser.UserId;
        if (userId is null) return Unauthorized();
        var result = await _reauthentication.VerifyPasswordAsync(
            userId.Value,
            request,
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            Request.Headers.UserAgent.ToString(),
            ct);
        // A wrong password is a 400, not a 401: the caller still holds a valid session, and clients
        // treat 401 as "signed out" and leave the page with the form the proof was for.
        return result.IsSuccess
            ? Ok(ApiResponse<object>.Ok(result.Data!))
            : BadRequest(ApiResponse<object>.Fail(result.ErrorCode, result.Message));
    }
}
