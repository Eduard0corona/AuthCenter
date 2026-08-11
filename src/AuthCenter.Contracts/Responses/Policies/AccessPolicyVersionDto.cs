namespace AuthCenter.Contracts.Responses.Policies;

public sealed class AccessPolicyVersionDto
{
    public Guid Id { get; init; }
    public Guid ApplicationSystemId { get; init; }
    public int VersionNumber { get; init; }
    public string Status { get; init; } = string.Empty;
    public int RuleCount { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? PublishedAt { get; init; }
}
