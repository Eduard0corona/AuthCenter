using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;

namespace AuthCenter.Client;

public static class AuthCenterAuthorizationExtensions
{
    public static AuthorizationPolicyBuilder RequireAuthCenterPermission(
        this AuthorizationPolicyBuilder policy,
        params string[] permissions)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ValidateValues(permissions, nameof(permissions));
        return policy.RequireAssertion(context =>
            permissions.All(permission => context.User.HasAuthCenterPermission(permission)));
    }

    public static AuthorizationPolicyBuilder RequireAuthCenterScope(
        this AuthorizationPolicyBuilder policy,
        params string[] scopes)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ValidateValues(scopes, nameof(scopes));
        return policy.RequireAssertion(context =>
            scopes.All(scope => context.User.HasAuthCenterScope(scope)));
    }

    public static bool HasAuthCenterPermission(this ClaimsPrincipal principal, string permission)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);
        return principal.FindAll(AuthCenterBffDefaults.PermissionClaim)
            .Any(item => string.Equals(item.Value, permission, StringComparison.Ordinal));
    }

    public static bool HasAuthCenterScope(this ClaimsPrincipal principal, string scope)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        return principal.FindAll("scope")
            .SelectMany(item => item.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Any(value => string.Equals(value, scope, StringComparison.Ordinal));
    }

    private static void ValidateValues(string[] values, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(values, parameterName);
        if (values.Length == 0 || values.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("At least one non-empty value is required.", parameterName);
    }
}
