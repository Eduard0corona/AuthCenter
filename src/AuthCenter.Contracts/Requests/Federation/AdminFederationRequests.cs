namespace AuthCenter.Contracts.Requests.Federation;

public sealed class UpdateFederationRoutingRuleRequest
{
    public int Priority { get; init; }
    public string? EmailDomain { get; init; }
    public Guid? DirectoryGroupId { get; init; }
    public Guid? ProfileAttributeDefinitionId { get; init; }
    public string? ExpectedProfileValueJson { get; init; }
    public bool IsActive { get; init; }
    public long Version { get; init; }
}

public sealed class ReorderFederationRoutingRulesRequest
{
    public IReadOnlyList<FederationRoutingRuleOrderItem> Rules { get; init; } = [];
}

public sealed class FederationRoutingRuleOrderItem
{
    public Guid Id { get; init; }
    public int Priority { get; init; }
    public long Version { get; init; }
}
