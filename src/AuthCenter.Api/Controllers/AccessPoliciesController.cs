using AuthCenter.Api.Filters;
using AuthCenter.Application.Common;
using AuthCenter.Application.Interfaces;
using AuthCenter.Contracts.Requests.Policies;
using AuthCenter.Contracts.Responses;
using AuthCenter.Domain.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuthCenter.Api.Controllers;

[ApiController]
[Route("api/access-policies")]
[Authorize]
public class AccessPoliciesController : ControllerBase
{
    private readonly IAccessPolicyService _policies;
    private readonly IReauthenticationService _reauthentication;
    private readonly ICurrentUserService _currentUser;
    private readonly IAuditService _audit;

    public AccessPoliciesController(
        IAccessPolicyService policies,
        IReauthenticationService reauthentication,
        ICurrentUserService currentUser,
        IAuditService audit)
    {
        _policies = policies;
        _reauthentication = reauthentication;
        _currentUser = currentUser;
        _audit = audit;
    }

    [HttpGet("applications/{applicationSystemId:guid}")]
    [Authorize(Policy = DomainConstants.Permissions.AccessPoliciesRead)]
    public async Task<IActionResult> GetByApplication(
        Guid applicationSystemId,
        [FromQuery] Guid? policyVersionId,
        CancellationToken ct) =>
        Ok(ApiResponse<object>.Ok(await _policies.GetByApplicationAsync(applicationSystemId, policyVersionId, ct)));

    [HttpGet("applications/{applicationSystemId:guid}/versions")]
    [Authorize(Policy = DomainConstants.Permissions.AccessPoliciesRead)]
    public async Task<IActionResult> GetVersions(Guid applicationSystemId, CancellationToken ct) =>
        Ok(ApiResponse<object>.Ok(await _policies.GetVersionsAsync(applicationSystemId, ct)));

    [Idempotent]
    [HttpPost("applications/{applicationSystemId:guid}/drafts")]
    [Authorize(Policy = DomainConstants.Permissions.AccessPoliciesWrite)]
    public async Task<IActionResult> CreateDraft(Guid applicationSystemId, CancellationToken ct)
    {
        var result = await _policies.CreateDraftAsync(applicationSystemId, ct);
        return result.IsSuccess
            ? Ok(ApiResponse<object>.Ok(result.Data!))
            : BadRequest(ApiResponse<object>.Fail(result.ErrorCode, result.Message));
    }

    [Idempotent]
    [HttpPost("applications/{applicationSystemId:guid}/versions/{policyVersionId:guid}/publish")]
    [Authorize(Policy = DomainConstants.Permissions.AccessPoliciesWrite)]
    public async Task<IActionResult> Publish(Guid applicationSystemId, Guid policyVersionId, CancellationToken ct)
    {
        const string purpose = "admin.access-policy.publish";
        if (!await HasReauthenticationProofAsync(purpose, ct))
        {
            await _audit.LogAsync(
                "ACCESS_POLICY_PUBLISH_REJECTED",
                userId: _currentUser.UserId,
                entityName: "ApplicationAccessPolicyVersion",
                entityId: policyVersionId.ToString(),
                ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
                userAgent: Request.Headers.UserAgent.ToString(),
                metadata: new { applicationSystemId, result = "Rejected", reason = "ReauthenticationRequired", purpose },
                ct: ct);
            return ReauthenticationRequired(purpose);
        }

        var result = await _policies.PublishAsync(applicationSystemId, policyVersionId, ct);
        return result.IsSuccess
            ? Ok(ApiResponse<object>.Ok(result.Data!))
            : BadRequest(ApiResponse<object>.Fail(result.ErrorCode, result.Message));
    }

    [HttpPost("simulate")]
    [Authorize(Policy = DomainConstants.Permissions.AccessPoliciesRead)]
    public async Task<IActionResult> Simulate(SimulateAccessPolicyRequest request, CancellationToken ct)
    {
        var result = await _policies.SimulateAsync(request, ct);
        return result.IsSuccess
            ? Ok(ApiResponse<object>.Ok(result.Data!))
            : BadRequest(ApiResponse<object>.Fail(result.ErrorCode, result.Message));
    }

    [Idempotent]
    [HttpPost]
    [Authorize(Policy = DomainConstants.Permissions.AccessPoliciesWrite)]
    public async Task<IActionResult> Create([FromBody] CreateAccessPolicyRuleRequest request, CancellationToken ct)
    {
        var result = await _policies.CreateAsync(request, ct);
        return result.IsSuccess
            ? Created($"/api/access-policies/{result.Data!.Id}", ApiResponse<object>.Ok(result.Data))
            : BadRequest(ApiResponse<object>.Fail(result.ErrorCode, result.Message));
    }

    [HttpPut("{ruleId:guid}")]
    [Authorize(Policy = DomainConstants.Permissions.AccessPoliciesWrite)]
    public async Task<IActionResult> Update(
        Guid ruleId,
        [FromBody] UpdateAccessPolicyRuleRequest request,
        CancellationToken ct)
    {
        var result = await _policies.UpdateAsync(ruleId, request, ct);
        return result.IsSuccess
            ? Ok(ApiResponse<object>.Ok(result.Data!))
            : BadRequest(ApiResponse<object>.Fail(result.ErrorCode, result.Message));
    }

    [HttpDelete("{ruleId:guid}")]
    [Authorize(Policy = DomainConstants.Permissions.AccessPoliciesWrite)]
    public async Task<IActionResult> Delete(Guid ruleId, CancellationToken ct) =>
        ToActionResult(await _policies.DeleteAsync(ruleId, ct));

    private IActionResult ToActionResult(OperationResult result) =>
        result.IsSuccess
            ? Ok(ApiResponse.Ok())
            : BadRequest(ApiResponse.Fail(result.ErrorCode, result.Message));

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
