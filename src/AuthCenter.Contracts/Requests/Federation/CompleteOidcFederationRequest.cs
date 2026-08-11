namespace AuthCenter.Contracts.Requests.Federation;

public sealed class CompleteOidcFederationRequest
{
    public string InteractionId { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;
    public string Code { get; init; } = string.Empty;
}
