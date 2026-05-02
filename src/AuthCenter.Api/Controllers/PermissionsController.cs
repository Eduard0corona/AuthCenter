using AuthCenter.Application.Interfaces;
using AuthCenter.Contracts.Requests.Common;
using AuthCenter.Contracts.Requests.Permissions;
using AuthCenter.Contracts.Responses;
using AuthCenter.Domain.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuthCenter.Api.Controllers;

[ApiController]
[Route("api/permissions")]
[Authorize]
public class PermissionsController : ControllerBase
{
    private readonly IPermissionService _permissionService;

    public PermissionsController(IPermissionService permissionService)
    {
        _permissionService = permissionService;
    }

    [HttpGet]
    [Authorize(Policy = DomainConstants.Permissions.PermissionsRead)]
    public async Task<IActionResult> GetAll([FromQuery] PaginationQuery pagination, CancellationToken ct)
    {
        var result = await _permissionService.GetAllAsync(pagination, ct);
        return Ok(ApiResponse<object>.Ok(result));
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = DomainConstants.Permissions.PermissionsRead)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var permission = await _permissionService.GetByIdAsync(id, ct);
        if (permission is null) return NotFound(ApiResponse<object>.Fail("NOT_FOUND", "Permission not found."));
        return Ok(ApiResponse<object>.Ok(permission));
    }

    [HttpGet("~/api/applications/{applicationId:guid}/permissions")]
    [Authorize(Policy = DomainConstants.Permissions.PermissionsRead)]
    public async Task<IActionResult> GetByApplication(Guid applicationId, [FromQuery] PaginationQuery pagination, CancellationToken ct)
    {
        var result = await _permissionService.GetByApplicationAsync(applicationId, pagination, ct);
        return Ok(ApiResponse<object>.Ok(result));
    }

    [HttpPost]
    [Authorize(Policy = DomainConstants.Permissions.PermissionsWrite)]
    public async Task<IActionResult> Create([FromBody] CreatePermissionRequest request, CancellationToken ct)
    {
        var result = await _permissionService.CreateAsync(request, ct);
        if (!result.IsSuccess) return BadRequest(ApiResponse<object>.Fail(result.ErrorCode, result.Message));
        return Created($"/api/permissions/{result.Data!.Id}", ApiResponse<object>.Ok(result.Data));
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = DomainConstants.Permissions.PermissionsWrite)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdatePermissionRequest request, CancellationToken ct)
    {
        var result = await _permissionService.UpdateAsync(id, request, ct);
        if (!result.IsSuccess) return BadRequest(ApiResponse<object>.Fail(result.ErrorCode, result.Message));
        return Ok(ApiResponse<object>.Ok(result.Data!));
    }

    [HttpPatch("{id:guid}/activate")]
    [Authorize(Policy = DomainConstants.Permissions.PermissionsWrite)]
    public async Task<IActionResult> Activate(Guid id, CancellationToken ct)
    {
        var result = await _permissionService.ActivateAsync(id, ct);
        if (!result.IsSuccess) return BadRequest(ApiResponse.Fail(result.ErrorCode, result.Message));
        return Ok(ApiResponse.Ok());
    }

    [HttpPatch("{id:guid}/deactivate")]
    [Authorize(Policy = DomainConstants.Permissions.PermissionsWrite)]
    public async Task<IActionResult> Deactivate(Guid id, CancellationToken ct)
    {
        var result = await _permissionService.DeactivateAsync(id, ct);
        if (!result.IsSuccess) return BadRequest(ApiResponse.Fail(result.ErrorCode, result.Message));
        return Ok(ApiResponse.Ok());
    }
}
