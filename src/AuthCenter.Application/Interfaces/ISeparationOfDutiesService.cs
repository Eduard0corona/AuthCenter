using AuthCenter.Application.Common;
using AuthCenter.Contracts.Requests.Governance;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Responses.Governance;

namespace AuthCenter.Application.Interfaces;

/// <summary>Separation of duties rules and the users who break them.</summary>
public interface ISeparationOfDutiesService
{
    Task<PagedResult<SeparationOfDutiesRuleDto>> GetRulesAsync(SeparationOfDutiesRuleQuery query, CancellationToken ct = default);
    Task<SeparationOfDutiesRuleDto?> GetRuleAsync(Guid ruleId, CancellationToken ct = default);
    Task<OperationResult<SeparationOfDutiesRuleDto>> CreateRuleAsync(SeparationOfDutiesRuleRequest request, CancellationToken ct = default);
    Task<OperationResult<SeparationOfDutiesRuleDto>> UpdateRuleAsync(Guid ruleId, SeparationOfDutiesRuleRequest request, CancellationToken ct = default);
    Task<OperationResult> DeleteRuleAsync(Guid ruleId, CancellationToken ct = default);
    Task<PagedResult<SeparationOfDutiesViolationDto>> GetViolationsAsync(SeparationOfDutiesViolationQuery query, CancellationToken ct = default);
    /// <summary>Users breaking an active rule (a user breaking two rules counts twice).</summary>
    Task<int> CountViolationsAsync(CancellationToken ct = default);
}
