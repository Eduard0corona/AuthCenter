namespace AuthCenter.Contracts.Requests.Federation;

public sealed class BeginSamlFederationRequest
{
    public Guid ProviderId { get; init; }
}
