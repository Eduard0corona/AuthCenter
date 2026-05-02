namespace AuthCenter.Domain.Entities;

public class RolePermission
{
    public Guid RoleId { get; set; }
    public Guid PermissionId { get; set; }
    public DateTime CreatedAt { get; set; }

    public ApplicationRole Role { get; set; } = null!;
    public Permission Permission { get; set; } = null!;
}
