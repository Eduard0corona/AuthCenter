using AuthCenter.Domain.Enums;

namespace AuthCenter.Domain.Entities;

public class ApplicationAccessPolicyVersion
{
    public Guid Id { get; set; }
    public Guid ApplicationSystemId { get; set; }
    public int VersionNumber { get; set; }
    public AccessPolicyVersionStatus Status { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public Guid? PublishedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? PublishedAt { get; set; }

    public ApplicationSystem ApplicationSystem { get; set; } = null!;
    public ApplicationUser? CreatedByUser { get; set; }
    public ApplicationUser? PublishedByUser { get; set; }
    public ICollection<ApplicationAccessPolicyRule> Rules { get; set; } = new List<ApplicationAccessPolicyRule>();
}
