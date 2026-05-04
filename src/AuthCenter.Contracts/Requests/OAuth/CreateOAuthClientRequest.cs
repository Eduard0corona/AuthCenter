namespace AuthCenter.Contracts.Requests.OAuth;

public class CreateOAuthClientRequest
{
    public string ClientId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public int ClientType { get; init; }      // 0 = Confidential, 1 = Public
    public IList<string> RedirectUris { get; init; } = [];
    public IList<string> AllowedScopes { get; init; } = [];
    public IList<string> GrantTypes { get; init; } = [];
    public string LoginUrl { get; init; } = string.Empty;
    public int AccessTokenLifetimeSeconds { get; init; } = 3600;
    public bool RequirePkce { get; init; } = true;
    public bool AutoConsent { get; init; }
}
