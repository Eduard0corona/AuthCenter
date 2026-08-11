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

    public AccessPoliciesController(IAccessPolicyService policies)
    {
        _policies = policies;
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

    [HttpPost("applications/{applicationSystemId:guid}/drafts")]
    [Authorize(Policy = DomainConstants.Permissions.AccessPoliciesWrite)]
    public async Task<IActionResult> CreateDraft(Guid applicationSystemId, CancellationToken ct)
    {
        var result = await _policies.CreateDraftAsync(applicationSystemId, ct);
        return result.IsSuccess
            ? Ok(ApiResponse<object>.Ok(result.Data!))
            : BadRequest(ApiResponse<object>.Fail(result.ErrorCode, result.Message));
    }

    [HttpPost("applications/{applicationSystemId:guid}/versions/{policyVersionId:guid}/publish")]
    [Authorize(Policy = DomainConstants.Permissions.AccessPoliciesWrite)]
    public async Task<IActionResult> Publish(Guid applicationSystemId, Guid policyVersionId, CancellationToken ct)
    {
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
}
