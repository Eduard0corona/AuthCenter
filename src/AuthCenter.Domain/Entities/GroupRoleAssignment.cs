namespace AuthCenter.Domain.Entities;

public class GroupRoleAssignment
{
    public Guid GroupId { get; set; }
    public Guid RoleId { get; set; }
    public DateTime CreatedAt { get; set; }

    public DirectoryGroup Group { get; set; } = null!;
    public ApplicationRole Role { get; set; } = null!;
}
