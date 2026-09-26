using AuthCenter.Domain.Enums;

namespace AuthCenter.Domain.Entities;

public class OAuthClient
{
    public Guid Id { get; set; }
    public Guid ApplicationSystemId { get; set; }
    public string ClientId { get; set; } = string.Empty;
    public string? HashedClientSecret { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string RedirectUrisJson { get; set; } = "[]";
    public string AllowedScopesJson { get; set; } = "[]";
    public string GrantTypesJson { get; set; } = "[]";
    public OAuthClientType ClientType { get; set; }
    public string LoginUrl { get; set; } = string.Empty;

    /// <summary>
    /// Browser origins (scheme://host[:port]) allowed to call the token, revocation and UserInfo
    /// endpoints with CORS, without credentials: single-page applications using PKCE.
    /// </summary>
    public string AllowedCorsOriginsJson { get; set; } = "[]";

    /// <summary>Exact URIs the browser may return to after RP-initiated logout.</summary>
    public string PostLogoutRedirectUrisJson { get; set; } = "[]";

    /// <summary>Where AuthCenter posts OpenID Connect back-channel logout tokens, if anywhere.</summary>
    public string? BackchannelLogoutUri { get; set; }

    /// <summary>Whether the client needs the sid claim in logout tokens (always sent by AuthCenter).</summary>
    public bool BackchannelLogoutSessionRequired { get; set; } = true;
    public int AccessTokenLifetimeSeconds { get; set; } = 900;
    public bool RequirePkce { get; set; } = true;
    public bool AutoConsent { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    public ApplicationSystem ApplicationSystem { get; set; } = null!;
    public ICollection<OAuthConsentGrant> ConsentGrants { get; set; } = new List<OAuthConsentGrant>();
    public ICollection<OAuthAuthorizationCode> AuthorizationCodes { get; set; } = new List<OAuthAuthorizationCode>();
}
