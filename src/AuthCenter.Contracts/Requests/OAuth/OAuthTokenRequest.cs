namespace AuthCenter.Contracts.Requests.OAuth;

public class OAuthTokenRequest
{
    public string? GrantType { get; init; }
    public string? Code { get; init; }
    public string? RedirectUri { get; init; }
    public string? ClientId { get; init; }
    public string? ClientSecret { get; init; }
    public string? CodeVerifier { get; init; }
    public string? Scope { get; init; }
    public string? RefreshToken { get; init; }
}
