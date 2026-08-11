namespace AuthCenter.Contracts.Requests.Lifecycle;

public sealed class CreateProvisioningTokenRequest
{
    public Guid ApplicationSystemId { get; init; }
    public string Name { get; init; } = string.Empty;
    public IReadOnlyList<string> Scopes { get; init; } = [];
    public DateTime ExpiresAt { get; init; }
}
