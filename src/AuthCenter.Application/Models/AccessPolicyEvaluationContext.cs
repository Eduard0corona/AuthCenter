using AuthCenter.Domain.Enums;

namespace AuthCenter.Application.Models;

public sealed record AccessPolicyEvaluationContext(
    Guid UserId,
    Guid ApplicationSystemId,
    string? IpAddress,
    DateTime EvaluatedAtUtc,
    AccessRiskLevel RiskLevel,
    AuthenticationAssuranceLevel AssuranceLevel,
    Guid? PolicyVersionId = null,
    bool IncludeExplanation = false);
