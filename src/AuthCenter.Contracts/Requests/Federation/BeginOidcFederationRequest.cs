namespace AuthCenter.Contracts.Requests.Federation;

public sealed class BeginOidcFederationRequest
{
    public Guid ProviderId { get; init; }
    public string? LoginHint { get; init; }
}
