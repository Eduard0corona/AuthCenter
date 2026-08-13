namespace AuthCenter.Domain.Entities;

public sealed class DynamicGroupRule
{
    public Guid Id { get; set; }
    public Guid DirectoryGroupId { get; set; }
    public Guid ProfileAttributeDefinitionId { get; set; }
    public string Operator { get; set; } = "eq";
    public string ExpectedValueJson { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public long Version { get; set; } = 1;
    public DirectoryGroup DirectoryGroup { get; set; } = null!;
    public UserProfileAttributeDefinition ProfileAttributeDefinition { get; set; } = null!;
}
