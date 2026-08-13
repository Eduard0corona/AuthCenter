namespace AuthCenter.Contracts.Responses.Federation;

public sealed class FederationRoutingRuleDto
{
    public Guid Id { get; init; }
    public Guid FederationProviderId { get; init; }
    public string ProviderName { get; init; } = string.Empty;
    public Guid ApplicationSystemId { get; init; }
    public int Priority { get; init; }
    public string? EmailDomain { get; init; }
    public Guid? DirectoryGroupId { get; init; }
    public Guid? ProfileAttributeDefinitionId { get; init; }
    public string? ExpectedProfileValueJson { get; init; }
    public bool IsActive { get; init; }
    public long Version { get; init; }
}
