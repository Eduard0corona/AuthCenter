namespace AuthCenter.Domain.Entities;

public class DirectoryGroup
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string NormalizedName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    public ICollection<UserGroupMembership> Memberships { get; set; } = new List<UserGroupMembership>();
    public ICollection<GroupApplicationAssignment> ApplicationAssignments { get; set; } = new List<GroupApplicationAssignment>();
    public ICollection<GroupRoleAssignment> RoleAssignments { get; set; } = new List<GroupRoleAssignment>();
    public ICollection<ApplicationAccessPolicyRule> AccessPolicyRules { get; set; } = new List<ApplicationAccessPolicyRule>();
}
