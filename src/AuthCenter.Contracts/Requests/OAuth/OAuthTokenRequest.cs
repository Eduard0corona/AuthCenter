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

    /// <summary>RFC 8707 resource indicator: the API the requested access token is for.</summary>
    public string? Resource { get; init; }

    /// <summary>RFC 8693 token exchange: the token being exchanged and its type.</summary>
    public string? SubjectToken { get; init; }
    public string? SubjectTokenType { get; init; }
    public string? RequestedTokenType { get; init; }

    /// <summary>RFC 8693 audience, accepted as an alternative to resource for token exchange.</summary>
    public string? Audience { get; init; }
}
