using AuthCenter.Application.Interfaces;
using AuthCenter.Contracts.Requests.Common;
using AuthCenter.Contracts.Responses;
using AuthCenter.Domain.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuthCenter.Api.Controllers;

[ApiController]
[Route("api/users")]
[Authorize]
public class UsersController : ControllerBase
{
    private readonly IUserAccessService _userAccessService;

    public UsersController(IUserAccessService userAccessService)
    {
        _userAccessService = userAccessService;
    }

    [HttpGet]
    [Authorize(Policy = DomainConstants.Permissions.UsersRead)]
    public async Task<IActionResult> GetAll([FromQuery] PaginationQuery pagination, CancellationToken ct)
    {
        var result = await _userAccessService.GetAllUsersAsync(pagination, ct);
        return Ok(ApiResponse<object>.Ok(result));
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = DomainConstants.Permissions.UsersRead)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var user = await _userAccessService.GetUserByIdAsync(id, ct);
        if (user is null) return NotFound(ApiResponse<object>.Fail("NOT_FOUND", "User not found."));
        return Ok(ApiResponse<object>.Ok(user));
    }

    [HttpPost("{id:guid}/applications/{applicationId:guid}")]
    [Authorize(Policy = DomainConstants.Permissions.UsersWrite)]
    public async Task<IActionResult> GrantAccess(Guid id, Guid applicationId, CancellationToken ct)
    {
        var result = await _userAccessService.GrantAccessAsync(id, applicationId, true, ct);
        if (!result.IsSuccess) return BadRequest(ApiResponse.Fail(result.ErrorCode, result.Message));
        return Ok(ApiResponse.Ok());
    }

    [HttpPatch("{id:guid}/applications/{applicationId:guid}/approve")]
    [Authorize(Policy = DomainConstants.Permissions.UsersWrite)]
    public async Task<IActionResult> ApproveAccess(Guid id, Guid applicationId, CancellationToken ct)
    {
        var result = await _userAccessService.ApproveApplicationAccessAsync(id, applicationId, ct);
        if (!result.IsSuccess) return BadRequest(ApiResponse.Fail(result.ErrorCode, result.Message));
        return Ok(ApiResponse.Ok());
    }

    [HttpDelete("{id:guid}/applications/{applicationId:guid}")]
    [Authorize(Policy = DomainConstants.Permissions.UsersWrite)]
    public async Task<IActionResult> RevokeAccess(Guid id, Guid applicationId, CancellationToken ct)
    {
        var result = await _userAccessService.RevokeAccessAsync(id, applicationId, ct);
        if (!result.IsSuccess) return BadRequest(ApiResponse.Fail(result.ErrorCode, result.Message));
        return Ok(ApiResponse.Ok());
    }

    [HttpPost("{id:guid}/roles/{roleId:guid}")]
    [Authorize(Policy = DomainConstants.Permissions.UsersWrite)]
    public async Task<IActionResult> AssignRole(Guid id, Guid roleId, CancellationToken ct)
    {
        var result = await _userAccessService.AssignRoleAsync(id, roleId, ct);
        if (!result.IsSuccess) return BadRequest(ApiResponse.Fail(result.ErrorCode, result.Message));
        return Ok(ApiResponse.Ok());
    }

    [HttpDelete("{id:guid}/roles/{roleId:guid}")]
    [Authorize(Policy = DomainConstants.Permissions.UsersWrite)]
    public async Task<IActionResult> RemoveRole(Guid id, Guid roleId, CancellationToken ct)
    {
        var result = await _userAccessService.RemoveRoleAsync(id, roleId, ct);
        if (!result.IsSuccess) return BadRequest(ApiResponse.Fail(result.ErrorCode, result.Message));
        return Ok(ApiResponse.Ok());
    }

    [HttpPatch("{id:guid}/activate")]
    [Authorize(Policy = DomainConstants.Permissions.UsersWrite)]
    public async Task<IActionResult> Activate(Guid id, CancellationToken ct)
    {
        var result = await _userAccessService.ActivateUserAsync(id, ct);
        if (!result.IsSuccess) return BadRequest(ApiResponse.Fail(result.ErrorCode, result.Message));
        return Ok(ApiResponse.Ok());
    }

    [HttpPatch("{id:guid}/deactivate")]
    [Authorize(Policy = DomainConstants.Permissions.UsersWrite)]
    public async Task<IActionResult> Deactivate(Guid id, CancellationToken ct)
    {
        var result = await _userAccessService.DeactivateUserAsync(id, ct);
        if (!result.IsSuccess) return BadRequest(ApiResponse.Fail(result.ErrorCode, result.Message));
        return Ok(ApiResponse.Ok());
    }
}
