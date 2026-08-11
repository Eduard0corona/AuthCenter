namespace AuthCenter.Domain.Entities;

public sealed class ScimResourceLink
{
    public Guid Id { get; set; }
    public Guid ApplicationSystemId { get; set; }
    public string ResourceType { get; set; } = string.Empty;
    public Guid ResourceId { get; set; }
    public string? ExternalId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public ApplicationSystem ApplicationSystem { get; set; } = null!;
}
