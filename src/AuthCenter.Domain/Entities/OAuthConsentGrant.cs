namespace AuthCenter.Domain.Entities;

public sealed class OAuthConsentGrant
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid OAuthClientId { get; set; }
    public string ScopesJson { get; set; } = "[]";
    public DateTime GrantedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public ApplicationUser User { get; set; } = null!;
    public OAuthClient OAuthClient { get; set; } = null!;
}
