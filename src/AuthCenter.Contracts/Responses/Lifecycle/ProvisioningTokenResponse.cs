namespace AuthCenter.Contracts.Responses.Lifecycle;

public sealed class ProvisioningTokenResponse
{
    public Guid Id { get; init; }
    public string Token { get; init; } = string.Empty;
    public IReadOnlyList<string> Scopes { get; init; } = [];
    public DateTime ExpiresAt { get; init; }
}
