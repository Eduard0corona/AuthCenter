using AuthCenter.Application.Interfaces;
using AuthCenter.Contracts.Requests.Lifecycle;
using AuthCenter.Contracts.Responses;
using AuthCenter.Domain.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuthCenter.Api.Controllers;

[ApiController, Authorize(Policy = DomainConstants.Permissions.ApplicationsWrite), Route("api/provisioning-tokens")]
public sealed class ProvisioningTokensController : ControllerBase
{
    private readonly IProvisioningTokenService _tokens; public ProvisioningTokensController(IProvisioningTokenService tokens) => _tokens = tokens;
    [HttpPost] public async Task<IActionResult> Create(CreateProvisioningTokenRequest request, CancellationToken ct) { var r = await _tokens.CreateAsync(request, ct); return r.IsSuccess ? Created($"/api/provisioning-tokens/{r.Data!.Id}", ApiResponse<object>.Ok(r.Data)) : BadRequest(ApiResponse<object>.Fail(r.ErrorCode, r.Message)); }
    [HttpPost("{id:guid}/rotate")] public async Task<IActionResult> Rotate(Guid id, [FromQuery] DateTime expiresAt, CancellationToken ct) { var r = await _tokens.RotateAsync(id, expiresAt, ct); return r.IsSuccess ? Ok(ApiResponse<object>.Ok(r.Data!)) : BadRequest(ApiResponse<object>.Fail(r.ErrorCode, r.Message)); }
    [HttpDelete("{id:guid}")] public async Task<IActionResult> Revoke(Guid id, CancellationToken ct) { var r = await _tokens.RevokeAsync(id, ct); return r.IsSuccess ? Ok(ApiResponse.Ok()) : BadRequest(ApiResponse.Fail(r.ErrorCode, r.Message)); }
}
