namespace AuthCenter.Contracts.Responses.Policies;

public class AccessPolicyRuleDto
{
    public Guid Id { get; init; }
    public Guid ApplicationSystemId { get; init; }
    public string ApplicationCode { get; init; } = string.Empty;
    public Guid? DirectoryGroupId { get; init; }
    public string? DirectoryGroupName { get; init; }
    public string Name { get; init; } = string.Empty;
    public int Priority { get; init; }
    public string Action { get; init; } = string.Empty;
    public string MfaRequirement { get; init; } = string.Empty;
    public bool AllowTrustedDeviceBypass { get; init; }
    public IReadOnlyList<string> IncludedIpCidrs { get; init; } = [];
    public IReadOnlyList<string> ExcludedIpCidrs { get; init; } = [];
    public bool IsActive { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? UpdatedAt { get; init; }
}
