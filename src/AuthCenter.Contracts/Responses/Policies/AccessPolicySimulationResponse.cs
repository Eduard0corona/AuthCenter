namespace AuthCenter.Contracts.Responses.Policies;

public sealed class AccessPolicySimulationResponse
{
    public bool IsAllowed { get; init; }
    public bool RequireMfa { get; init; }
    public bool AllowTrustedDeviceBypass { get; init; }
    public string RequiredAssuranceLevel { get; init; } = string.Empty;
    public Guid? MatchedRuleId { get; init; }
    public string? MatchedRuleName { get; init; }
    public string DecisionReason { get; init; } = string.Empty;
    public Guid? PolicyVersionId { get; init; }
    public int? PolicyVersionNumber { get; init; }
    public string? PolicyVersionStatus { get; init; }
    public IReadOnlyList<AccessPolicyRuleEvaluationDto> RuleEvaluations { get; init; } = [];
}

public sealed class AccessPolicyRuleEvaluationDto
{
    public Guid RuleId { get; init; }
    public string RuleName { get; init; } = string.Empty;
    public int Priority { get; init; }
    public bool Matched { get; init; }
    public IReadOnlyList<string> Reasons { get; init; } = [];
}
