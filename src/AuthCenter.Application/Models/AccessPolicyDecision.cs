using AuthCenter.Domain.Enums;

namespace AuthCenter.Application.Models;

public sealed record AccessPolicyDecision(
    bool IsAllowed,
    bool RequireMfa,
    bool AllowTrustedDeviceBypass,
    AuthenticationAssuranceLevel RequiredAssuranceLevel,
    Guid? MatchedRuleId,
    string? MatchedRuleName,
    string DecisionReason,
    Guid? PolicyVersionId,
    int? PolicyVersionNumber,
    AccessPolicyVersionStatus? PolicyVersionStatus,
    IReadOnlyList<AccessPolicyRuleEvaluation> RuleEvaluations)
{
    public static AccessPolicyDecision AllowByDefault { get; } = new(
        true,
        false,
        true,
        AuthenticationAssuranceLevel.Password,
        null,
        null,
        "No published policy exists; access is allowed by default.",
        null,
        null,
        null,
        []);
}

public sealed record AccessPolicyRuleEvaluation(
    Guid RuleId,
    string RuleName,
    int Priority,
    bool Matched,
    IReadOnlyList<string> Reasons);
