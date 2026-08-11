namespace AuthCenter.Domain.Entities;

public sealed class ProfileMapping
{
    public Guid Id { get; set; }
    public Guid ApplicationSystemId { get; set; }
    public string SourceSystem { get; set; } = "SCIM";
    public string SourcePath { get; set; } = string.Empty;
    public Guid TargetAttributeDefinitionId { get; set; }
    public bool IsAuthoritative { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public ApplicationSystem ApplicationSystem { get; set; } = null!;
    public UserProfileAttributeDefinition TargetAttributeDefinition { get; set; } = null!;
}
