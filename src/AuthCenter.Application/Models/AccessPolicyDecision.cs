namespace AuthCenter.Application.Models;

public sealed record AccessPolicyDecision(
    bool IsAllowed,
    bool RequireMfa,
    bool AllowTrustedDeviceBypass,
    Guid? MatchedRuleId,
    string? MatchedRuleName)
{
    public static AccessPolicyDecision AllowByDefault { get; } = new(true, false, true, null, null);
}
