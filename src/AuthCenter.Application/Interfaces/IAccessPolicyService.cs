using AuthCenter.Application.Common;
using AuthCenter.Application.Models;
using AuthCenter.Contracts.Requests.Policies;
using AuthCenter.Contracts.Responses.Policies;

namespace AuthCenter.Application.Interfaces;

public interface IAccessPolicyService
{
    Task<IReadOnlyList<AccessPolicyRuleDto>> GetByApplicationAsync(Guid applicationSystemId, CancellationToken ct = default);
    Task<OperationResult<AccessPolicyRuleDto>> CreateAsync(CreateAccessPolicyRuleRequest request, CancellationToken ct = default);
    Task<OperationResult<AccessPolicyRuleDto>> UpdateAsync(Guid ruleId, UpdateAccessPolicyRuleRequest request, CancellationToken ct = default);
    Task<OperationResult> DeleteAsync(Guid ruleId, CancellationToken ct = default);
    Task<AccessPolicyDecision> EvaluateAsync(Guid userId, Guid applicationSystemId, string? ipAddress, CancellationToken ct = default);
}
