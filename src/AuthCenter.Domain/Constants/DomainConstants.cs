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

        public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
        {
            UsersRead, UsersWrite, ApplicationsRead, ApplicationsWrite, RolesRead, RolesWrite,
            PermissionsRead, PermissionsWrite, AuditLogsRead, OAuthClientsRead, OAuthClientsWrite
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
    }

    public static class OAuthScopes
    {
        public const string OpenId = "openid";
        public const string Profile = "profile";
        public const string Email = "email";
        public const string OfflineAccess = "offline_access";

        public static readonly IReadOnlyList<string> All = [OpenId, Profile, Email, OfflineAccess];
    }
}
