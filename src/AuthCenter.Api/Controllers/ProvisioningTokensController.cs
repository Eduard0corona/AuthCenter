using AuthCenter.Api.Filters;
using AuthCenter.Application.Interfaces;
using AuthCenter.Contracts.Requests.Lifecycle;
using AuthCenter.Contracts.Responses;
using AuthCenter.Domain.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuthCenter.Api.Controllers;

[ApiController, Authorize, Route("api/provisioning-tokens")]
public sealed class ProvisioningTokensController : ControllerBase
{
    private readonly IProvisioningTokenService _tokens; private readonly IReauthenticationService _reauthentication; private readonly ICurrentUserService _currentUser; private readonly IAuditService _audit;
    public ProvisioningTokensController(IProvisioningTokenService tokens, IReauthenticationService reauthentication, ICurrentUserService currentUser, IAuditService audit) => (_tokens, _reauthentication, _currentUser, _audit) = (tokens, reauthentication, currentUser, audit);

    [HttpGet, Authorize(Policy = DomainConstants.Permissions.ProvisioningRead)] public async Task<IActionResult> Get([FromQuery] ProvisioningTokenQuery query, CancellationToken ct) => Ok(ApiResponse<object>.Ok(await _tokens.GetAsync(query, ct)));
    [HttpGet("{id:guid}"), Authorize(Policy = DomainConstants.Permissions.ProvisioningRead)] public async Task<IActionResult> GetById(Guid id, CancellationToken ct) { var item = await _tokens.GetByIdAsync(id, ct); return item is null ? NotFound(ApiResponse<object>.Fail("PROVISIONING_TOKEN_NOT_FOUND", "Provisioning token not found.")) : Ok(ApiResponse<object>.Ok(item)); }
    [HttpGet("{id:guid}/diagnostics"), Authorize(Policy = DomainConstants.Permissions.ProvisioningRead)] public async Task<IActionResult> Diagnostics(Guid id, CancellationToken ct) { var item = await _tokens.GetDiagnosticsAsync(id, ct); return item is null ? NotFound(ApiResponse<object>.Fail("PROVISIONING_TOKEN_NOT_FOUND", "Token not found.")) : Ok(ApiResponse<object>.Ok(item)); }
    [HttpGet("{id:guid}/requests"), Authorize(Policy = DomainConstants.Permissions.ProvisioningRead)] public async Task<IActionResult> Requests(Guid id, [FromQuery] ScimRequestLogQuery query, CancellationToken ct) { var page = await _tokens.GetRequestsAsync(id, query, ct); return page is null ? NotFound(ApiResponse<object>.Fail("PROVISIONING_TOKEN_NOT_FOUND", "Token not found.")) : Ok(ApiResponse<object>.Ok(page)); }
    [HttpPost, Idempotent, Authorize(Policy = DomainConstants.Permissions.ProvisioningWrite)] public async Task<IActionResult> Create(CreateProvisioningTokenRequest request, CancellationToken ct) { var r = await _tokens.CreateAsync(request, ct); return r.IsSuccess ? Created($"/api/provisioning-tokens/{r.Data!.Id}", ApiResponse<object>.Ok(r.Data)) : BadRequest(ApiResponse<object>.Fail(r.ErrorCode, r.Message)); }
    [HttpPost("{id:guid}/rotate"), Idempotent, Authorize(Policy = DomainConstants.Permissions.ProvisioningWrite)] public async Task<IActionResult> Rotate(Guid id, [FromQuery] DateTime expiresAt, CancellationToken ct) { const string purpose = "admin.provisioning-token.rotate"; if (!await HasProofAsync(purpose, ct)) return await RejectedAsync(id, purpose, ct); var r = await _tokens.RotateAsync(id, expiresAt, ct); return r.IsSuccess ? Ok(ApiResponse<object>.Ok(r.Data!)) : BadRequest(ApiResponse<object>.Fail(r.ErrorCode, r.Message)); }
    [HttpDelete("{id:guid}"), Authorize(Policy = DomainConstants.Permissions.ProvisioningWrite)] public async Task<IActionResult> Revoke(Guid id, CancellationToken ct) { const string purpose = "admin.provisioning-token.revoke"; if (!await HasProofAsync(purpose, ct)) return await RejectedAsync(id, purpose, ct); var r = await _tokens.RevokeAsync(id, ct); return r.IsSuccess ? Ok(ApiResponse.Ok()) : BadRequest(ApiResponse.Fail(r.ErrorCode, r.Message)); }
    private Task<bool> HasProofAsync(string purpose, CancellationToken ct) => _currentUser.UserId.HasValue ? _reauthentication.ConsumeProofAsync(_currentUser.UserId.Value, purpose, Request.Headers["X-AuthCenter-Reauthentication"].ToString(), ct) : Task.FromResult(false);
    private async Task<ObjectResult> RejectedAsync(Guid id, string purpose, CancellationToken ct) { await _audit.LogAsync("PROVISIONING_TOKEN_MUTATION_REJECTED", entityName: "ProvisioningToken", entityId: id.ToString(), metadata: new { result = "Rejected", reason = "ReauthenticationRequired", purpose }, ct: ct); return StatusCode(StatusCodes.Status403Forbidden, ApiResponse.Fail("REAUTHENTICATION_REQUIRED", $"A recent single-use reauthentication proof for {purpose} is required.")); }
}
