namespace AuthCenter.Contracts.Requests.OAuth;

public class UpdateOAuthClientRequest
{
    public string DisplayName { get; init; } = string.Empty;
    public IList<string> RedirectUris { get; init; } = [];
    public IList<string> AllowedScopes { get; init; } = [];
    public IList<string> GrantTypes { get; init; } = [];
    public string LoginUrl { get; init; } = string.Empty;
    public int AccessTokenLifetimeSeconds { get; init; } = 900;
    public bool RequirePkce { get; init; } = true;
    public bool AutoConsent { get; init; }
    public bool IsActive { get; init; } = true;
}
