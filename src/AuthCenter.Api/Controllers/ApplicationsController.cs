using AuthCenter.Application.Interfaces;
using AuthCenter.Contracts.Requests.Applications;
using AuthCenter.Contracts.Requests.Common;
using AuthCenter.Contracts.Responses;
using AuthCenter.Domain.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuthCenter.Api.Controllers;

[ApiController]
[Route("api/applications")]
[Authorize]
public class ApplicationsController : ControllerBase
{
    private readonly IApplicationService _applicationService;

    public ApplicationsController(IApplicationService applicationService)
    {
        _applicationService = applicationService;
    }

    [HttpGet]
    [Authorize(Policy = DomainConstants.Permissions.ApplicationsRead)]
    public async Task<IActionResult> GetAll([FromQuery] PaginationQuery pagination, CancellationToken ct)
    {
        var result = await _applicationService.GetAllAsync(pagination, ct);
        return Ok(ApiResponse<object>.Ok(result));
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = DomainConstants.Permissions.ApplicationsRead)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var app = await _applicationService.GetByIdAsync(id, ct);
        if (app is null) return NotFound(ApiResponse<object>.Fail("NOT_FOUND", "Application not found."));
        return Ok(ApiResponse<object>.Ok(app));
    }

    [HttpPost]
    [Authorize(Policy = DomainConstants.Permissions.ApplicationsWrite)]
    public async Task<IActionResult> Create([FromBody] CreateApplicationRequest request, CancellationToken ct)
    {
        var result = await _applicationService.CreateAsync(request, ct);
        if (!result.IsSuccess)
            return BadRequest(ApiResponse<object>.Fail(result.ErrorCode, result.Message));
        return CreatedAtAction(nameof(GetById), new { id = result.Data!.Id }, ApiResponse<object>.Ok(result.Data));
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = DomainConstants.Permissions.ApplicationsWrite)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateApplicationRequest request, CancellationToken ct)
    {
        var result = await _applicationService.UpdateAsync(id, request, ct);
        if (!result.IsSuccess)
            return BadRequest(ApiResponse<object>.Fail(result.ErrorCode, result.Message));
        return Ok(ApiResponse<object>.Ok(result.Data!));
    }

    [HttpPatch("{id:guid}/activate")]
    [Authorize(Policy = DomainConstants.Permissions.ApplicationsWrite)]
    public async Task<IActionResult> Activate(Guid id, CancellationToken ct)
    {
        var result = await _applicationService.ActivateAsync(id, ct);
        if (!result.IsSuccess)
            return BadRequest(ApiResponse.Fail(result.ErrorCode, result.Message));
        return Ok(ApiResponse.Ok());
    }

    [HttpPatch("{id:guid}/deactivate")]
    [Authorize(Policy = DomainConstants.Permissions.ApplicationsWrite)]
    public async Task<IActionResult> Deactivate(Guid id, CancellationToken ct)
    {
        var result = await _applicationService.DeactivateAsync(id, ct);
        if (!result.IsSuccess)
            return BadRequest(ApiResponse.Fail(result.ErrorCode, result.Message));
        return Ok(ApiResponse.Ok());
    }
}
