namespace AuthCenter.Contracts.Responses.OAuth;

public class OAuthInteractionResponse
{
    public string ClientId { get; init; } = string.Empty;
    public string ClientDisplayName { get; init; } = string.Empty;
    public string ApplicationCode { get; init; } = string.Empty;
    public string ApplicationName { get; init; } = string.Empty;
    public IList<string> Scopes { get; init; } = [];
    public bool RequiresConsent { get; init; }

    /// <summary>The current session is too old for the request (prompt=login or max_age).</summary>
    public bool RequiresReauthentication { get; init; }

    public DateTime ExpiresAt { get; init; }
}
