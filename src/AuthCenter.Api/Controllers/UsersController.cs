using AuthCenter.Application.Interfaces;
using AuthCenter.Contracts.Requests.Common;
using AuthCenter.Contracts.Requests.Users;
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
    private readonly IReauthenticationService _reauthentication;
    private readonly ICurrentUserService _currentUser;

    public UsersController(
        IUserAccessService userAccessService,
        IReauthenticationService reauthentication,
        ICurrentUserService currentUser)
    {
        _userAccessService = userAccessService;
        _reauthentication = reauthentication;
        _currentUser = currentUser;
    }

    [HttpGet]
    [Authorize(Policy = DomainConstants.Permissions.UsersRead)]
    public async Task<IActionResult> GetAll([FromQuery] UserQuery pagination, CancellationToken ct)
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

    [HttpPost]
    [Authorize(Policy = DomainConstants.Permissions.UsersWrite)]
    public async Task<IActionResult> Create([FromBody] CreateUserRequest request, CancellationToken ct)
    {
        var result = await _userAccessService.CreateUserAsync(request, ct);
        if (!result.IsSuccess) return BadRequest(ApiResponse<object>.Fail(result.ErrorCode, result.Message));
        return CreatedAtAction(nameof(GetById), new { id = result.Data!.Id }, ApiResponse<object>.Ok(result.Data));
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = DomainConstants.Permissions.UsersWrite)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateUserRequest request, CancellationToken ct)
    {
        var result = await _userAccessService.UpdateUserAsync(id, request, ct);
        if (!result.IsSuccess) return BadRequest(ApiResponse<object>.Fail(result.ErrorCode, result.Message));
        return Ok(ApiResponse<object>.Ok(result.Data!));
    }

    [HttpPut("{id:guid}/access")]
    [Authorize(Policy = DomainConstants.Permissions.UsersWrite)]
    public async Task<IActionResult> SetDirectAccess(Guid id, [FromBody] SetUserDirectAccessRequest request, CancellationToken ct)
    {
        var result = await _userAccessService.SetDirectAccessAsync(id, request, ct);
        return result.IsSuccess
            ? Ok(ApiResponse<object>.Ok(result.Data!))
            : BadRequest(ApiResponse<object>.Fail(result.ErrorCode, result.Message));
    }

    [HttpPost("invitations")]
    [Authorize(Policy = DomainConstants.Permissions.UsersWrite)]
    public async Task<IActionResult> Invite([FromBody] InviteUserRequest request, CancellationToken ct)
    {
        var result = await _userAccessService.InviteUserAsync(request, ct);
        if (!result.IsSuccess) return BadRequest(ApiResponse<object>.Fail(result.ErrorCode, result.Message));
        return Ok(ApiResponse<object>.Ok(result.Data!));
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

    [HttpPost("{id:guid}/force-password-change")]
    [Authorize(Policy = DomainConstants.Permissions.UsersWrite)]
    public async Task<IActionResult> ForcePasswordChange(Guid id, CancellationToken ct)
    {
        var result = await _userAccessService.ForcePasswordChangeAsync(id, ct);
        if (!result.IsSuccess) return BadRequest(ApiResponse.Fail(result.ErrorCode, result.Message));
        return Ok(ApiResponse.Ok());
    }

    [HttpDelete("{id:guid}/mfa")]
    [Authorize(Policy = DomainConstants.Permissions.UsersWrite)]
    public async Task<IActionResult> AdminResetMfa(Guid id, [FromServices] IMfaService mfaService, CancellationToken ct)
    {
        if (!await HasReauthenticationProofAsync("admin.mfa.reset", ct))
            return ReauthenticationRequired("admin.mfa.reset");
        var result = await mfaService.AdminResetMfaAsync(id, ct);
        if (!result.IsSuccess) return BadRequest(ApiResponse.Fail(result.ErrorCode, result.Message));
        return Ok(ApiResponse.Ok());
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = DomainConstants.Permissions.UsersWrite)]
    public async Task<IActionResult> AdminDelete(Guid id, CancellationToken ct)
    {
        if (!await HasReauthenticationProofAsync("admin.user.delete", ct))
            return ReauthenticationRequired("admin.user.delete");
        var result = await _userAccessService.AdminDeleteUserAsync(id, ct);
        if (!result.IsSuccess) return BadRequest(ApiResponse.Fail(result.ErrorCode, result.Message));
        return Ok(ApiResponse.Ok());
    }

    private async Task<bool> HasReauthenticationProofAsync(string purpose, CancellationToken ct)
    {
        var currentUserId = _currentUser.UserId;
        return currentUserId.HasValue && await _reauthentication.ConsumeProofAsync(
            currentUserId.Value, purpose, Request.Headers["X-AuthCenter-Reauthentication"].ToString(), ct);
    }

    private ObjectResult ReauthenticationRequired(string purpose) => StatusCode(
        StatusCodes.Status403Forbidden,
        ApiResponse.Fail("REAUTHENTICATION_REQUIRED", $"A recent single-use reauthentication proof for {purpose} is required."));
}
