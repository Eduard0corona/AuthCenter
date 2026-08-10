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
    public async Task<IActionResult> GetByApplication(Guid applicationSystemId, CancellationToken ct) =>
        Ok(ApiResponse<object>.Ok(await _policies.GetByApplicationAsync(applicationSystemId, ct)));

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
