namespace AuthCenter.Contracts.Responses.Federation;

public sealed class FederationRouteResponse
{
    public Guid ProviderId { get; init; }
    public string ProviderName { get; init; } = string.Empty;
    public string Protocol { get; init; } = string.Empty;
}
