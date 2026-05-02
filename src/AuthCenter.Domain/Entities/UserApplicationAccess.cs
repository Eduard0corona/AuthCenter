namespace AuthCenter.Domain.Entities;

public class UserApplicationAccess
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid ApplicationSystemId { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime? RevokedAt { get; set; }

    public ApplicationUser User { get; set; } = null!;
    public ApplicationSystem ApplicationSystem { get; set; } = null!;
}
