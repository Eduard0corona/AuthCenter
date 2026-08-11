namespace AuthCenter.Domain.Entities;

public class UserProfileAttributeValue
{
    public Guid UserId { get; set; }
    public Guid AttributeDefinitionId { get; set; }
    public string ValueJson { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    public ApplicationUser User { get; set; } = null!;
    public UserProfileAttributeDefinition AttributeDefinition { get; set; } = null!;
}
