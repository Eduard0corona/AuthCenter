namespace AuthCenter.Contracts.Requests.OAuth;

/// <summary>RFC 7662 token introspection request from an authenticated confidential client.</summary>
public class OAuthIntrospectionRequest
{
    public string? Token { get; init; }
    public string? TokenTypeHint { get; init; }
    public string? ClientId { get; init; }
    public string? ClientSecret { get; init; }
}
