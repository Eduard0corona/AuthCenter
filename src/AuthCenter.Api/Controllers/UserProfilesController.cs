using AuthCenter.Application.Interfaces;
using AuthCenter.Contracts.Requests.Profiles;
using AuthCenter.Contracts.Responses;
using AuthCenter.Domain.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuthCenter.Api.Controllers;

[ApiController]
[Route("api/users/{userId:guid}/profile")]
[Authorize]
public sealed class UserProfilesController : ControllerBase
{
    private readonly IUserProfileService _profiles;

    public UserProfilesController(IUserProfileService profiles)
    {
        _profiles = profiles;
    }

    [HttpGet]
    [Authorize(Policy = DomainConstants.Permissions.UsersRead)]
    public async Task<IActionResult> Get(Guid userId, CancellationToken ct)
    {
        var result = await _profiles.GetUserProfileAsync(userId, ct);
        return result.IsSuccess
            ? Ok(ApiResponse<object>.Ok(result.Data!))
            : NotFound(ApiResponse<object>.Fail(result.ErrorCode, result.Message));
    }

    [HttpPut]
    [Authorize(Policy = DomainConstants.Permissions.UsersWrite)]
    public async Task<IActionResult> Update(Guid userId, UpdateUserProfileRequest request, CancellationToken ct)
    {
        var result = await _profiles.UpdateUserProfileAsync(userId, request, ct);
        return result.IsSuccess
            ? Ok(ApiResponse<object>.Ok(result.Data!))
            : BadRequest(ApiResponse<object>.Fail(result.ErrorCode, result.Message));
    }
}
