namespace AuthCenter.Domain.Entities;

public class GroupApplicationAssignment
{
    public Guid GroupId { get; set; }
    public Guid ApplicationSystemId { get; set; }
    public DateTime CreatedAt { get; set; }

    public DirectoryGroup Group { get; set; } = null!;
    public ApplicationSystem ApplicationSystem { get; set; } = null!;
}
