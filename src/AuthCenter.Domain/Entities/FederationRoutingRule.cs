namespace AuthCenter.Domain.Entities;

public sealed class FederationRoutingRule
{
    public Guid Id { get; set; }
    public Guid FederationProviderId { get; set; }
    public int Priority { get; set; }
    public string? EmailDomain { get; set; }
    public Guid? DirectoryGroupId { get; set; }
    public Guid? ProfileAttributeDefinitionId { get; set; }
    public string? ExpectedProfileValueJson { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public FederationProvider FederationProvider { get; set; } = null!;
    public DirectoryGroup? DirectoryGroup { get; set; }
    public UserProfileAttributeDefinition? ProfileAttributeDefinition { get; set; }
}
