namespace AuthCenter.Contracts.Requests.OAuth;

public class OAuthRevocationRequest
{
    public string? Token { get; init; }
    public string? TokenTypeHint { get; init; }
    public string? ClientId { get; init; }
    public string? ClientSecret { get; init; }
}
