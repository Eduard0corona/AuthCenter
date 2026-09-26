namespace AuthCenter.Client;

public static class AuthCenterBffDefaults
{
    public const string CookieScheme = "AuthCenter.Bff.Cookie";
    public const string OpenIdConnectScheme = "AuthCenter.Bff.Oidc";
    public const string AntiforgeryHeaderName = "X-AuthCenter-CSRF";
    public const string PermissionClaim = "permissions";
    public const string ApplicationsClaim = "applications";
    public const string RoleClaim = "role";
    public const string AccessTokenType = "at+jwt";
    public const string LogoutTokenType = "logout+jwt";
    public const string BackchannelLogoutEvent = "http://schemas.openid.net/event/backchannel-logout";
}
