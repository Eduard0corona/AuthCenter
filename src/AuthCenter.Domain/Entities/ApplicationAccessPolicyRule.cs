using AuthCenter.Domain.Enums;

namespace AuthCenter.Domain.Entities;

public class ApplicationAccessPolicyRule
{
    public Guid Id { get; set; }
    public Guid PolicyVersionId { get; set; }
    public Guid ApplicationSystemId { get; set; }
    public Guid? UserId { get; set; }
    public Guid? DirectoryGroupId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Priority { get; set; }
    public AccessPolicyAction Action { get; set; }
    public AccessPolicyMfaRequirement MfaRequirement { get; set; }
    public bool AllowTrustedDeviceBypass { get; set; } = true;
    public string? IncludedIpCidrsJson { get; set; }
    public string? ExcludedIpCidrsJson { get; set; }
    public DateTime? ActiveFromUtc { get; set; }
    public DateTime? ActiveUntilUtc { get; set; }
    public string? ActiveDaysUtcJson { get; set; }
    public TimeOnly? DailyStartTimeUtc { get; set; }
    public TimeOnly? DailyEndTimeUtc { get; set; }
    public AccessRiskLevel? MinimumRiskLevel { get; set; }
    public AccessRiskLevel? MaximumRiskLevel { get; set; }
    public AuthenticationAssuranceLevel RequiredAssuranceLevel { get; set; } = AuthenticationAssuranceLevel.Password;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    public ApplicationAccessPolicyVersion PolicyVersion { get; set; } = null!;
    public ApplicationSystem ApplicationSystem { get; set; } = null!;
    public ApplicationUser? User { get; set; }
    public DirectoryGroup? DirectoryGroup { get; set; }
}
