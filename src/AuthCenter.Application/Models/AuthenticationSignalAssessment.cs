using AuthCenter.Domain.Enums;

namespace AuthCenter.Application.Models;

public sealed record AuthenticationSignalAssessment(AccessRiskLevel RiskLevel, IReadOnlyList<string> ReasonCodes);
