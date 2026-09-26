using AuthCenter.Domain.Common;
using Microsoft.AspNetCore.Identity;

namespace AuthCenter.Domain.Entities;

public class ApplicationRole : IdentityRole<Guid>, IVersionedEntity
{
    public long Version { get; set; }

    public string DisplayName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public Guid? ApplicationSystemId { get; set; }
    public bool IsSystemRole { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }

    public ApplicationSystem? ApplicationSystem { get; set; }
    public ICollection<RolePermission> RolePermissions { get; set; } = new List<RolePermission>();
    public ICollection<GroupRoleAssignment> GroupAssignments { get; set; } = new List<GroupRoleAssignment>();
}
