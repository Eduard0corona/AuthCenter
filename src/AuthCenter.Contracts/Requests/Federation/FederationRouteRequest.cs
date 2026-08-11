namespace AuthCenter.Contracts.Requests.Federation;

public sealed class FederationRouteRequest
{
    public string ApplicationCode { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
}
