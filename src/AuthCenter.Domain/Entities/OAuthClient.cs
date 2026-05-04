using AuthCenter.Domain.Enums;

namespace AuthCenter.Domain.Entities;

public class OAuthClient
{
    public Guid Id { get; set; }
    public string ClientId { get; set; } = string.Empty;
    public string? HashedClientSecret { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string RedirectUrisJson { get; set; } = "[]";
    public string AllowedScopesJson { get; set; } = "[]";
    public string GrantTypesJson { get; set; } = "[]";
    public OAuthClientType ClientType { get; set; }
    public string LoginUrl { get; set; } = string.Empty;
    public int AccessTokenLifetimeSeconds { get; set; } = 3600;
    public bool RequirePkce { get; set; } = true;
    public bool AutoConsent { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    public ICollection<OAuthAuthorizationCode> AuthorizationCodes { get; set; } = new List<OAuthAuthorizationCode>();
}
