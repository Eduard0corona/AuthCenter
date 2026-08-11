namespace AuthCenter.Api.Authorization;

public static class AuthenticationSchemes
{
    public const string Smart = "Smart";
    public const string UiCookie = "UiCookie";
    /// <summary>
    /// Bearer scheme for access tokens issued by the OAuth token endpoint. These carry the
    /// requesting client as their audience instead of <c>Jwt:Audience</c>, so they cannot be
    /// validated by the default scheme.
    /// </summary>
    public const string OAuthBearer = "OAuthBearer";
}
