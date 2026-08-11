namespace AuthCenter.Contracts.Responses.OAuth;

public class OAuthInteractionResponse
{
    public string ClientId { get; init; } = string.Empty;
    public string ClientDisplayName { get; init; } = string.Empty;
    public string ApplicationCode { get; init; } = string.Empty;
    public string ApplicationName { get; init; } = string.Empty;
    public IList<string> Scopes { get; init; } = [];
    public bool RequiresConsent { get; init; }
    public DateTime ExpiresAt { get; init; }
}
