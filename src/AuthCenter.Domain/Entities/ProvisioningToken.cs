namespace AuthCenter.Domain.Entities;

public sealed class ProvisioningToken
{
    public Guid Id { get; set; }
    public Guid ApplicationSystemId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string TokenHash { get; set; } = string.Empty;
    public string ScopesJson { get; set; } = "[]";
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public ApplicationSystem ApplicationSystem { get; set; } = null!;
}
