namespace AuthCenter.Contracts.Responses.OAuth;

public class OAuthClientResponse
{
    /// <summary>Send it back when updating: an update of an older version is rejected with 409.</summary>
    public long Version { get; init; }

    public Guid Id { get; init; }
    public Guid ApplicationSystemId { get; init; }
    public string ApplicationCode { get; init; } = string.Empty;
    public string ApplicationName { get; init; } = string.Empty;
    public string ClientId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public int ClientType { get; init; }      // 0 = Confidential, 1 = Public
    public IList<string> RedirectUris { get; init; } = [];
    public IList<string> AllowedScopes { get; init; } = [];
    public IList<string> GrantTypes { get; init; } = [];
    public string LoginUrl { get; init; } = string.Empty;
    public IList<string> PostLogoutRedirectUris { get; init; } = [];
    public IList<string> AllowedCorsOrigins { get; init; } = [];
    public string? BackchannelLogoutUri { get; init; }
    public bool BackchannelLogoutSessionRequired { get; init; }
    public int AccessTokenLifetimeSeconds { get; init; }
    public bool RequirePkce { get; init; }
    public bool AutoConsent { get; init; }
    public bool IsActive { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? UpdatedAt { get; init; }
}
