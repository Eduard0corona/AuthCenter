using AuthCenter.Domain.Enums;

namespace AuthCenter.Domain.Entities;

public class ApplicationAccessPolicyRule
{
    public Guid Id { get; set; }
    public Guid ApplicationSystemId { get; set; }
    public Guid? DirectoryGroupId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Priority { get; set; }
    public AccessPolicyAction Action { get; set; }
    public AccessPolicyMfaRequirement MfaRequirement { get; set; }
    public bool AllowTrustedDeviceBypass { get; set; } = true;
    public string? IncludedIpCidrsJson { get; set; }
    public string? ExcludedIpCidrsJson { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    public ApplicationSystem ApplicationSystem { get; set; } = null!;
    public DirectoryGroup? DirectoryGroup { get; set; }
}
