using AuthCenter.Domain.Common;
namespace AuthCenter.Domain.Entities;

public class Permission : IVersionedEntity
{
    public long Version { get; set; }

    public Guid Id { get; set; }
    public Guid ApplicationSystemId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }

    public ApplicationSystem ApplicationSystem { get; set; } = null!;
    public ICollection<RolePermission> RolePermissions { get; set; } = new List<RolePermission>();
}
