namespace AuthCenter.Contracts.Responses.OAuth;

public sealed class OAuthConsentGrantDto
{
    public Guid Id { get; init; }
    public string ClientId { get; init; } = string.Empty;
    public string ClientDisplayName { get; init; } = string.Empty;
    public string ApplicationCode { get; init; } = string.Empty;
    public string ApplicationName { get; init; } = string.Empty;
    public IReadOnlyList<string> Scopes { get; init; } = [];
    public DateTime GrantedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}
