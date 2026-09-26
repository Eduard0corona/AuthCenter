namespace AuthCenter.Application.Models;

public class OAuthAuthorizationSession
{
    public Guid ApplicationSystemId { get; init; }
    public string ApplicationCode { get; init; } = string.Empty;
    public string ClientId { get; init; } = string.Empty;
    public string RedirectUri { get; init; } = string.Empty;
    public IList<string> Scopes { get; init; } = [];

    /// <summary>API resource identifiers the requested API scopes belong to (RFC 8707).</summary>
    public IList<string> Resources { get; init; } = [];
    public string? State { get; init; }
    public string? CodeChallenge { get; init; }
    public string? CodeChallengeMethod { get; init; }
    public string? Nonce { get; init; }
    public DateTime CreatedAt { get; init; }
    public IList<string> Prompt { get; init; } = [];
    public int? MaxAge { get; init; }
    public string? LoginHint { get; init; }
    public string? IdTokenHintSubject { get; init; }
    public IList<string> AcrValues { get; init; } = [];
    public string ResponseMode { get; init; } = AuthorizationResponse.Query;

    /// <summary>SHA-256 of the browser binding cookie of the browser that started the request.</summary>
    public string? BrowserBindingHash { get; init; }
}
