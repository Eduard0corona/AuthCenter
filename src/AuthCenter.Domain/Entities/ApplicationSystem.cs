namespace AuthCenter.Domain.Entities;

public class ApplicationSystem
{
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    public ICollection<Permission> Permissions { get; set; } = new List<Permission>();
    public ICollection<UserApplicationAccess> UserApplicationAccesses { get; set; } = new List<UserApplicationAccess>();
    public ApplicationRegistrationSettings? RegistrationSettings { get; set; }
    public ICollection<ApplicationRole> Roles { get; set; } = new List<ApplicationRole>();
}
