namespace AuthCenter.Application.Models;

public class OAuthAuthorizationSession
{
    public string ClientId { get; init; } = string.Empty;
    public string RedirectUri { get; init; } = string.Empty;
    public IList<string> Scopes { get; init; } = [];
    public string? State { get; init; }
    public string? CodeChallenge { get; init; }
    public string? CodeChallengeMethod { get; init; }
    public string? Nonce { get; init; }
    public DateTime CreatedAt { get; init; }
}
