namespace AuthCenter.Domain.Entities;

public class UserGroupMembership
{
    public Guid GroupId { get; set; }
    public Guid UserId { get; set; }
    public DateTime CreatedAt { get; set; }

    public DirectoryGroup Group { get; set; } = null!;
    public ApplicationUser User { get; set; } = null!;
}
