namespace AuthCenter.Contracts.Requests.Policies;

public class UpdateAccessPolicyRuleRequest
{
    public Guid? UserId { get; init; }
    public Guid? DirectoryGroupId { get; init; }
    public string Name { get; init; } = string.Empty;
    public int Priority { get; init; }
    public string Action { get; init; } = "Allow";
    public string MfaRequirement { get; init; } = "Optional";
    public bool AllowTrustedDeviceBypass { get; init; } = true;
    public IReadOnlyList<string> IncludedIpCidrs { get; init; } = [];
    public IReadOnlyList<string> ExcludedIpCidrs { get; init; } = [];
    public DateTime? ActiveFromUtc { get; init; }
    public DateTime? ActiveUntilUtc { get; init; }
    public IReadOnlyList<string> ActiveDaysUtc { get; init; } = [];
    public TimeOnly? DailyStartTimeUtc { get; init; }
    public TimeOnly? DailyEndTimeUtc { get; init; }
    public string? MinimumRiskLevel { get; init; }
    public string? MaximumRiskLevel { get; init; }
    public string RequiredAssuranceLevel { get; init; } = "Password";
    public bool IsActive { get; init; } = true;
}
