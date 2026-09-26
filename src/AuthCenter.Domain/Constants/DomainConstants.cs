namespace AuthCenter.Domain.Constants;

public static class DomainConstants
{
    public static class Providers
    {
        public const string Google = "Google";
        public const string Microsoft = "Microsoft";
        public const string GitHub = "GitHub";
        public const string Apple = "Apple";
    }

    public static class SystemCodes
    {
        public const string AuthCenter = "AUTHCENTER";
    }

    public static class Permissions
    {
        /// <summary>
        /// Prefix of AuthCenter's own administration permissions. Permission codes are only unique
        /// per application, so the prefix is reserved to the AuthCenter application.
        /// </summary>
        public const string ReservedPrefix = "AUTHCENTER_";

        public const string UsersRead = "AUTHCENTER_USERS_READ";
        public const string UsersWrite = "AUTHCENTER_USERS_WRITE";
        public const string ApplicationsRead = "AUTHCENTER_APPLICATIONS_READ";
        public const string ApplicationsWrite = "AUTHCENTER_APPLICATIONS_WRITE";
        public const string RolesRead = "AUTHCENTER_ROLES_READ";
        public const string RolesWrite = "AUTHCENTER_ROLES_WRITE";
        public const string PermissionsRead = "AUTHCENTER_PERMISSIONS_READ";
        public const string PermissionsWrite = "AUTHCENTER_PERMISSIONS_WRITE";
        public const string AuditLogsRead = "AUTHCENTER_AUDIT_LOGS_READ";
        public const string OAuthClientsRead = "AUTHCENTER_OAUTH_CLIENTS_READ";
        public const string OAuthClientsWrite = "AUTHCENTER_OAUTH_CLIENTS_WRITE";
        public const string GroupsRead = "AUTHCENTER_GROUPS_READ";
        public const string GroupsWrite = "AUTHCENTER_GROUPS_WRITE";
        public const string AccessPoliciesRead = "AUTHCENTER_ACCESS_POLICIES_READ";
        public const string AccessPoliciesWrite = "AUTHCENTER_ACCESS_POLICIES_WRITE";
        public const string ProfileSchemasRead = "AUTHCENTER_PROFILE_SCHEMAS_READ";
        public const string ProfileSchemasWrite = "AUTHCENTER_PROFILE_SCHEMAS_WRITE";

        public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
        {
            UsersRead, UsersWrite, ApplicationsRead, ApplicationsWrite, RolesRead, RolesWrite,
            PermissionsRead, PermissionsWrite, AuditLogsRead, OAuthClientsRead, OAuthClientsWrite,
            GroupsRead, GroupsWrite, AccessPoliciesRead, AccessPoliciesWrite,
            ProfileSchemasRead, ProfileSchemasWrite
        };
    }

    public static class Roles
    {
        public const string SuperAdmin = "SuperAdmin";
        public const string Admin = "Admin";
    }

    public static class Claims
    {
        public const string Permissions = "permissions";
        public const string Applications = "applications";

        /// <summary>
        /// Short, interoperable role claim (RFC 9068 section 7.2 style) emitted in access tokens.
        /// Tokens issued before this contract used the .NET URI
        /// http://schemas.microsoft.com/ws/2008/06/identity/claims/role.
        /// </summary>
        public const string Role = "role";

        /// <summary>JWT header type for access tokens (RFC 9068), distinct from ID tokens.</summary>
        public const string AccessTokenType = "at+jwt";

        /// <summary>JWT header type for OpenID Connect back-channel logout tokens.</summary>
        public const string LogoutTokenType = "logout+jwt";

        /// <summary>Member of the logout token's events claim (OpenID Connect Back-Channel Logout 1.0).</summary>
        public const string BackchannelLogoutEvent = "http://schemas.openid.net/event/backchannel-logout";
    }

    /// <summary>RFC 8176 authentication method reference values emitted in the OIDC amr claim.</summary>
    public static class AuthenticationMethods
    {
        public const string Password = "pwd";
        public const string OneTimePassword = "otp";
        public const string MultiFactor = "mfa";

        /// <summary>Proof of possession of a key: a user-verified passkey (WebAuthn).</summary>
        public const string ProofOfPossession = "pop";

        /// <summary>Authentication delegated to an upstream identity provider (federation, social).</summary>
        public const string Federated = "fed";
    }

    /// <summary>OIDC acr values published by AuthCenter, one per authentication assurance level.</summary>
    public static class AuthenticationContextClasses
    {
        public const string SingleFactor = "urn:authcenter:acr:1fa";
        public const string MultiFactor = "urn:authcenter:acr:mfa";
        public const string PhishingResistant = "urn:authcenter:acr:phr";

        public static readonly IReadOnlyList<string> All = [SingleFactor, MultiFactor, PhishingResistant];
    }

    public static class OAuthScopes
    {
        public const string OpenId = "openid";
        public const string Profile = "profile";
        public const string Email = "email";
        public const string OfflineAccess = "offline_access";

        public static readonly IReadOnlyList<string> All = [OpenId, Profile, Email, OfflineAccess];
    }

    public static class OAuthGrantTypes
    {
        public const string AuthorizationCode = "authorization_code";
        public const string ClientCredentials = "client_credentials";
        public const string RefreshToken = "refresh_token";

        /// <summary>RFC 8693 token exchange, for an API calling another API on behalf of the user.</summary>
        public const string TokenExchange = "urn:ietf:params:oauth:grant-type:token-exchange";

        public static readonly IReadOnlyList<string> All = [AuthorizationCode, ClientCredentials, RefreshToken, TokenExchange];
    }

    public static class OAuthTokenTypes
    {
        public const string AccessToken = "urn:ietf:params:oauth:token-type:access_token";
    }

    public static class OAuthAudiences
    {
        /// <summary>
        /// Added to an access token for an API when openid was granted, so the same token can still
        /// call the UserInfo endpoint (as the OpenID Connect handler of a client does after sign-in).
        /// </summary>
        public const string UserInfo = "urn:authcenter:userinfo";
    }
}
