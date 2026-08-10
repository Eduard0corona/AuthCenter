namespace AuthCenter.Contracts.Requests.Policies;

public class CreateAccessPolicyRuleRequest
{
    public Guid ApplicationSystemId { get; init; }
    public Guid? DirectoryGroupId { get; init; }
    public string Name { get; init; } = string.Empty;
    public int Priority { get; init; }
    public string Action { get; init; } = "Allow";
    public string MfaRequirement { get; init; } = "Optional";
    public bool AllowTrustedDeviceBypass { get; init; } = true;
    public IReadOnlyList<string> IncludedIpCidrs { get; init; } = [];
    public IReadOnlyList<string> ExcludedIpCidrs { get; init; } = [];
    public bool IsActive { get; init; } = true;
}
