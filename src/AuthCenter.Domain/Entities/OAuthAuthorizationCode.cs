namespace AuthCenter.Domain.Entities;

public class OAuthAuthorizationCode
{
    public Guid Id { get; set; }
    public string CodeHash { get; set; } = string.Empty;
    public Guid OAuthClientId { get; set; }
    public Guid UserId { get; set; }
    public string RedirectUri { get; set; } = string.Empty;
    public string ScopesJson { get; set; } = "[]";

    /// <summary>API resource identifiers the authorization covers (RFC 8707).</summary>
    public string ResourcesJson { get; set; } = "[]";
    public string? CodeChallenge { get; set; }
    public string? CodeChallengeMethod { get; set; }
    public string? Nonce { get; set; }
    public DateTime ExpiresAt { get; set; }
    public bool IsUsed { get; set; }
    public Guid? SessionId { get; set; }
    public DateTime? AuthenticatedAt { get; set; }
    public string? AuthenticationMethods { get; set; }
    public int? AssuranceLevel { get; set; }
    public DateTime CreatedAt { get; set; }

    public OAuthClient OAuthClient { get; set; } = null!;
    public ApplicationUser User { get; set; } = null!;
}
