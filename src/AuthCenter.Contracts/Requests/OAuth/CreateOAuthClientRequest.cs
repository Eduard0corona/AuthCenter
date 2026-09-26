namespace AuthCenter.Contracts.Requests.OAuth;

public class CreateOAuthClientRequest
{
    public Guid ApplicationSystemId { get; init; }
    public string ClientId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public int ClientType { get; init; }      // 0 = Confidential, 1 = Public
    public IList<string> RedirectUris { get; init; } = [];
    public IList<string> AllowedScopes { get; init; } = [];
    public IList<string> GrantTypes { get; init; } = [];
    public string LoginUrl { get; init; } = string.Empty;
    public IList<string> PostLogoutRedirectUris { get; init; } = [];

    /// <summary>Browser origins allowed to call the token, revocation and UserInfo endpoints (CORS).</summary>
    public IList<string> AllowedCorsOrigins { get; init; } = [];
    public string? BackchannelLogoutUri { get; init; }
    public bool BackchannelLogoutSessionRequired { get; init; } = true;
    public int AccessTokenLifetimeSeconds { get; init; } = 900;
    public bool RequirePkce { get; init; } = true;
    public bool AutoConsent { get; init; }
}
