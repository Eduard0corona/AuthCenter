namespace AuthCenter.Contracts.Responses.Federation;

public sealed class SamlFederationChallengeResponse
{
    public string RedirectUrl { get; init; } = string.Empty;
    public int ExpiresIn { get; init; }
}
