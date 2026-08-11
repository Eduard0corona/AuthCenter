namespace AuthCenter.Contracts.Responses.Federation;

public sealed class OidcFederationChallengeResponse
{
    public string InteractionId { get; init; } = string.Empty;
    public string AuthorizationUrl { get; init; } = string.Empty;
    public int ExpiresIn { get; init; }
}
