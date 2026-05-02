namespace AuthCenter.Domain.Constants;

public static class DomainConstants
{
    public static class Providers
    {
        public const string Google = "Google";
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
}
