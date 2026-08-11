namespace AuthCenter.Contracts.Requests.Federation;

public sealed class CompleteSamlFederationRequest
{
    public string SamlResponse { get; init; } = string.Empty;
    public string RelayState { get; init; } = string.Empty;
}
