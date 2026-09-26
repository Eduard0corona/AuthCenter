using AuthCenter.Api.Filters;
using AuthCenter.Application.Interfaces;
using AuthCenter.Contracts.Requests.ApiResources;
using AuthCenter.Contracts.Responses;
using AuthCenter.Domain.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuthCenter.Api.Controllers;

/// <summary>APIs protected by AuthCenter (RFC 8707 resources) and the scopes clients can request.</summary>
[ApiController]
[Route("api/api-resources")]
[Authorize]
public class ApiResourcesController : ControllerBase
{
    private readonly IApiResourceService _resources;

    public ApiResourcesController(IApiResourceService resources) => _resources = resources;

    [HttpGet]
    [Authorize(Policy = DomainConstants.Permissions.OAuthClientsRead)]
    public async Task<IActionResult> GetAll([FromQuery] ApiResourceQuery query, CancellationToken ct) =>
        Ok(ApiResponse<object>.Ok(await _resources.GetAllAsync(query, ct)));

    [HttpGet("{id:guid}")]
    [Authorize(Policy = DomainConstants.Permissions.OAuthClientsRead)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var resource = await _resources.GetByIdAsync(id, ct);
        return resource is null
            ? NotFound(ApiResponse<object>.Fail("NOT_FOUND", "API not found."))
            : Ok(ApiResponse<object>.Ok(resource));
    }

    [Idempotent]
    [HttpPost]
    [Authorize(Policy = DomainConstants.Permissions.OAuthClientsWrite)]
    public async Task<IActionResult> Create([FromBody] CreateApiResourceRequest request, CancellationToken ct)
    {
        var result = await _resources.CreateAsync(request, ct);
        return result.IsSuccess
            ? CreatedAtAction(nameof(GetById), new { id = result.Data!.Id }, ApiResponse<object>.Ok(result.Data))
            : BadRequest(ApiResponse<object>.Fail(result.ErrorCode, result.Message));
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = DomainConstants.Permissions.OAuthClientsWrite)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateApiResourceRequest request, CancellationToken ct)
    {
        var result = await _resources.UpdateAsync(id, request, ct);
        if (result.IsSuccess)
            return Ok(ApiResponse<object>.Ok(result.Data!));
        return result.ErrorCode == "NOT_FOUND"
            ? NotFound(ApiResponse<object>.Fail(result.ErrorCode, result.Message))
            : BadRequest(ApiResponse<object>.Fail(result.ErrorCode, result.Message));
    }
}
