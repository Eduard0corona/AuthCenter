namespace AuthCenter.Domain.Entities;

/// <summary>A user accountable for an application: approves its access requests and reviews its access.</summary>
public class ApplicationOwner
{
    public Guid Id { get; set; }
    public Guid ApplicationSystemId { get; set; }
    public Guid UserId { get; set; }
    public DateTime CreatedAt { get; set; }

    public ApplicationSystem ApplicationSystem { get; set; } = null!;
    public ApplicationUser User { get; set; } = null!;
}
