using AuthCenter.Application.Common;
using AuthCenter.Application.Interfaces;
using AuthCenter.Contracts.Requests.Groups;
using AuthCenter.Contracts.Responses;
using AuthCenter.Domain.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuthCenter.Api.Controllers;

[ApiController]
[Route("api/groups")]
[Authorize]
public class GroupsController : ControllerBase
{
    private readonly IDirectoryGroupService _groups;

    public GroupsController(IDirectoryGroupService groups)
    {
        _groups = groups;
    }

    [HttpGet]
    [Authorize(Policy = DomainConstants.Permissions.GroupsRead)]
    public async Task<IActionResult> GetAll([FromQuery] DirectoryGroupQuery query, CancellationToken ct) =>
        Ok(ApiResponse<object>.Ok(await _groups.GetAllAsync(query, ct)));

    [HttpGet("{groupId:guid}")]
    [Authorize(Policy = DomainConstants.Permissions.GroupsRead)]
    public async Task<IActionResult> GetById(Guid groupId, CancellationToken ct)
    {
        var group = await _groups.GetByIdAsync(groupId, ct);
        return group is null
            ? NotFound(ApiResponse<object>.Fail("GROUP_NOT_FOUND", "Group not found."))
            : Ok(ApiResponse<object>.Ok(group));
    }

    [HttpGet("{groupId:guid}/members")]
    [Authorize(Policy = DomainConstants.Permissions.GroupsRead)]
    public async Task<IActionResult> GetMembers(
        Guid groupId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        var members = await _groups.GetMembersAsync(groupId, page, pageSize, ct);
        return members is null
            ? NotFound(ApiResponse<object>.Fail("GROUP_NOT_FOUND", "Group not found."))
            : Ok(ApiResponse<object>.Ok(members));
    }

    [HttpPost]
    [Authorize(Policy = DomainConstants.Permissions.GroupsWrite)]
    public async Task<IActionResult> Create([FromBody] CreateDirectoryGroupRequest request, CancellationToken ct)
    {
        var result = await _groups.CreateAsync(request, ct);
        return result.IsSuccess
            ? CreatedAtAction(nameof(GetById), new { groupId = result.Data!.Id }, ApiResponse<object>.Ok(result.Data))
            : BadRequest(ApiResponse<object>.Fail(result.ErrorCode, result.Message));
    }

    [HttpPut("{groupId:guid}")]
    [Authorize(Policy = DomainConstants.Permissions.GroupsWrite)]
    public async Task<IActionResult> Update(
        Guid groupId,
        [FromBody] UpdateDirectoryGroupRequest request,
        CancellationToken ct)
    {
        var result = await _groups.UpdateAsync(groupId, request, ct);
        return result.IsSuccess
            ? Ok(ApiResponse<object>.Ok(result.Data!))
            : BadRequest(ApiResponse<object>.Fail(result.ErrorCode, result.Message));
    }

    [HttpPatch("{groupId:guid}/activate")]
    [Authorize(Policy = DomainConstants.Permissions.GroupsWrite)]
    public async Task<IActionResult> Activate(Guid groupId, CancellationToken ct) =>
        ToActionResult(await _groups.ActivateAsync(groupId, ct));

    [HttpPatch("{groupId:guid}/deactivate")]
    [Authorize(Policy = DomainConstants.Permissions.GroupsWrite)]
    public async Task<IActionResult> Deactivate(Guid groupId, CancellationToken ct) =>
        ToActionResult(await _groups.DeactivateAsync(groupId, ct));

    [HttpPost("{groupId:guid}/members/{userId:guid}")]
    [Authorize(Policy = DomainConstants.Permissions.GroupsWrite)]
    public async Task<IActionResult> AddMember(Guid groupId, Guid userId, CancellationToken ct) =>
        ToActionResult(await _groups.AddMemberAsync(groupId, userId, ct));

    [HttpDelete("{groupId:guid}/members/{userId:guid}")]
    [Authorize(Policy = DomainConstants.Permissions.GroupsWrite)]
    public async Task<IActionResult> RemoveMember(Guid groupId, Guid userId, CancellationToken ct) =>
        ToActionResult(await _groups.RemoveMemberAsync(groupId, userId, ct));

    [HttpPost("{groupId:guid}/applications/{applicationSystemId:guid}")]
    [Authorize(Policy = DomainConstants.Permissions.GroupsWrite)]
    public async Task<IActionResult> AssignApplication(Guid groupId, Guid applicationSystemId, CancellationToken ct) =>
        ToActionResult(await _groups.AssignApplicationAsync(groupId, applicationSystemId, ct));

    [HttpDelete("{groupId:guid}/applications/{applicationSystemId:guid}")]
    [Authorize(Policy = DomainConstants.Permissions.GroupsWrite)]
    public async Task<IActionResult> RemoveApplication(Guid groupId, Guid applicationSystemId, CancellationToken ct) =>
        ToActionResult(await _groups.RemoveApplicationAsync(groupId, applicationSystemId, ct));

    [HttpPost("{groupId:guid}/roles/{roleId:guid}")]
    [Authorize(Policy = DomainConstants.Permissions.GroupsWrite)]
    public async Task<IActionResult> AssignRole(Guid groupId, Guid roleId, CancellationToken ct) =>
        ToActionResult(await _groups.AssignRoleAsync(groupId, roleId, ct));

    [HttpDelete("{groupId:guid}/roles/{roleId:guid}")]
    [Authorize(Policy = DomainConstants.Permissions.GroupsWrite)]
    public async Task<IActionResult> RemoveRole(Guid groupId, Guid roleId, CancellationToken ct) =>
        ToActionResult(await _groups.RemoveRoleAsync(groupId, roleId, ct));

    [HttpPut("{groupId:guid}/access")]
    [Authorize(Policy = DomainConstants.Permissions.GroupsWrite)]
    public async Task<IActionResult> SetAccess(
        Guid groupId,
        [FromBody] SetDirectoryGroupAccessRequest request,
        CancellationToken ct)
    {
        var result = await _groups.SetAccessAsync(groupId, request, ct);
        return result.IsSuccess
            ? Ok(ApiResponse<object>.Ok(result.Data!))
            : BadRequest(ApiResponse<object>.Fail(result.ErrorCode, result.Message));
    }

    private IActionResult ToActionResult(OperationResult result) =>
        result.IsSuccess
            ? Ok(ApiResponse.Ok())
            : BadRequest(ApiResponse.Fail(result.ErrorCode, result.Message));
}
