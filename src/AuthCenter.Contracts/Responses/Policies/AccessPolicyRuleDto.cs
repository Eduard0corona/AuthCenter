namespace AuthCenter.Contracts.Responses.Policies;

public class AccessPolicyRuleDto
{
    public Guid Id { get; init; }
    public Guid ApplicationSystemId { get; init; }
    public Guid PolicyVersionId { get; init; }
    public int PolicyVersionNumber { get; init; }
    public string PolicyVersionStatus { get; init; } = string.Empty;
    public string ApplicationCode { get; init; } = string.Empty;
    public Guid? UserId { get; init; }
    public string? UserEmail { get; init; }
    public Guid? DirectoryGroupId { get; init; }
    public string? DirectoryGroupName { get; init; }
    public string Name { get; init; } = string.Empty;
    public int Priority { get; init; }
    public string Action { get; init; } = string.Empty;
    public string MfaRequirement { get; init; } = string.Empty;
    public bool AllowTrustedDeviceBypass { get; init; }
    public IReadOnlyList<string> IncludedIpCidrs { get; init; } = [];
    public IReadOnlyList<string> ExcludedIpCidrs { get; init; } = [];
    public DateTime? ActiveFromUtc { get; init; }
    public DateTime? ActiveUntilUtc { get; init; }
    public IReadOnlyList<string> ActiveDaysUtc { get; init; } = [];
    public TimeOnly? DailyStartTimeUtc { get; init; }
    public TimeOnly? DailyEndTimeUtc { get; init; }
    public string? MinimumRiskLevel { get; init; }
    public string? MaximumRiskLevel { get; init; }
    public string RequiredAssuranceLevel { get; init; } = string.Empty;
    public bool IsActive { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? UpdatedAt { get; init; }
}
