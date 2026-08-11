namespace AuthCenter.Client;

public static class AuthCenterBffDefaults
{
    public const string CookieScheme = "AuthCenter.Bff.Cookie";
    public const string OpenIdConnectScheme = "AuthCenter.Bff.Oidc";
    public const string AntiforgeryHeaderName = "X-AuthCenter-CSRF";
    public const string PermissionClaim = "permissions";
    public const string ApplicationsClaim = "applications";
    public const string RoleClaim = "role";
}
