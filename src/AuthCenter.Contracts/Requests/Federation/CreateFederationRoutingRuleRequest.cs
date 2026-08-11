namespace AuthCenter.Contracts.Requests.Federation;

public sealed class CreateFederationRoutingRuleRequest
{
    public Guid FederationProviderId { get; init; }
    public int Priority { get; init; }
    public string? EmailDomain { get; init; }
    public Guid? DirectoryGroupId { get; init; }
    public Guid? ProfileAttributeDefinitionId { get; init; }
    public string? ExpectedProfileValueJson { get; init; }
    public bool IsActive { get; init; } = true;
}
