namespace AuthCenter.Domain.Entities;

/// <summary>
/// A client that received tokens through a single sign-on session. Ending the session notifies
/// these clients (OpenID Connect back-channel logout), even after their authorization codes are
/// purged by retention.
/// </summary>
public class SingleSignOnSessionClient
{
    public Guid SessionId { get; set; }
    public Guid OAuthClientId { get; set; }
    public Guid UserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime LastIssuedAt { get; set; }

    public OAuthClient OAuthClient { get; set; } = null!;
}
