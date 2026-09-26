using AuthCenter.Api.Filters;
using AuthCenter.Application.Interfaces;
using AuthCenter.Contracts.Requests.Common;
using AuthCenter.Contracts.Requests.Roles;
using AuthCenter.Contracts.Responses;
using AuthCenter.Domain.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuthCenter.Api.Controllers;

[ApiController]
[Route("api/roles")]
[Authorize]
public class RolesController : ControllerBase
{
    private readonly IRoleService _roleService;

    public RolesController(IRoleService roleService)
    {
        _roleService = roleService;
    }

    [HttpGet]
    [Authorize(Policy = DomainConstants.Permissions.RolesRead)]
    public async Task<IActionResult> GetAll([FromQuery] PaginationQuery pagination, [FromQuery] Guid? applicationSystemId, CancellationToken ct)
    {
        var result = await _roleService.GetAllAsync(pagination, applicationSystemId, ct);
        return Ok(ApiResponse<object>.Ok(result));
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = DomainConstants.Permissions.RolesRead)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var role = await _roleService.GetByIdAsync(id, ct);
        if (role is null) return NotFound(ApiResponse<object>.Fail("NOT_FOUND", "Role not found."));
        return Ok(ApiResponse<object>.Ok(role));
    }

    [Idempotent]
    [HttpPost]
    [Authorize(Policy = DomainConstants.Permissions.RolesWrite)]
    public async Task<IActionResult> Create([FromBody] CreateRoleRequest request, CancellationToken ct)
    {
        var result = await _roleService.CreateAsync(request, ct);
        if (!result.IsSuccess) return BadRequest(ApiResponse<object>.Fail(result.ErrorCode, result.Message));
        return CreatedAtAction(nameof(GetById), new { id = result.Data!.Id }, ApiResponse<object>.Ok(result.Data));
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = DomainConstants.Permissions.RolesWrite)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateRoleRequest request, CancellationToken ct)
    {
        var result = await _roleService.UpdateAsync(id, request, ct);
        if (!result.IsSuccess) return BadRequest(ApiResponse<object>.Fail(result.ErrorCode, result.Message));
        return Ok(ApiResponse<object>.Ok(result.Data!));
    }

    [HttpPost("{roleId:guid}/permissions/{permissionId:guid}")]
    [Authorize(Policy = DomainConstants.Permissions.RolesWrite)]
    public async Task<IActionResult> AddPermission(Guid roleId, Guid permissionId, CancellationToken ct)
    {
        var result = await _roleService.AddPermissionAsync(roleId, permissionId, ct);
        if (!result.IsSuccess) return BadRequest(ApiResponse.Fail(result.ErrorCode, result.Message));
        return Ok(ApiResponse.Ok());
    }

    [HttpDelete("{roleId:guid}/permissions/{permissionId:guid}")]
    [Authorize(Policy = DomainConstants.Permissions.RolesWrite)]
    public async Task<IActionResult> RemovePermission(Guid roleId, Guid permissionId, CancellationToken ct)
    {
        var result = await _roleService.RemovePermissionAsync(roleId, permissionId, ct);
        if (!result.IsSuccess) return BadRequest(ApiResponse.Fail(result.ErrorCode, result.Message));
        return Ok(ApiResponse.Ok());
    }

    [HttpPut("{roleId:guid}/permissions")]
    [Authorize(Policy = DomainConstants.Permissions.RolesWrite)]
    public async Task<IActionResult> SetPermissions(Guid roleId, [FromBody] SetRolePermissionsRequest request, CancellationToken ct)
    {
        var result = await _roleService.SetPermissionsAsync(roleId, request, ct);
        if (!result.IsSuccess) return BadRequest(ApiResponse<object>.Fail(result.ErrorCode, result.Message));
        return Ok(ApiResponse<object>.Ok(result.Data!));
    }

    [HttpPatch("{id:guid}/activate")]
    [Authorize(Policy = DomainConstants.Permissions.RolesWrite)]
    public async Task<IActionResult> Activate(Guid id, CancellationToken ct)
    {
        var result = await _roleService.ActivateAsync(id, ct);
        if (!result.IsSuccess) return BadRequest(ApiResponse.Fail(result.ErrorCode, result.Message));
        return Ok(ApiResponse.Ok());
    }

    [HttpPatch("{id:guid}/deactivate")]
    [Authorize(Policy = DomainConstants.Permissions.RolesWrite)]
    public async Task<IActionResult> Deactivate(Guid id, CancellationToken ct)
    {
        var result = await _roleService.DeactivateAsync(id, ct);
        if (!result.IsSuccess) return BadRequest(ApiResponse.Fail(result.ErrorCode, result.Message));
        return Ok(ApiResponse.Ok());
    }
}
