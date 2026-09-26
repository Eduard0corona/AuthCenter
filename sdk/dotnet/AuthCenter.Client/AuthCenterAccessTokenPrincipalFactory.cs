using System.Security.Claims;

namespace AuthCenter.Client;

internal static class AuthCenterAccessTokenPrincipalFactory
{
    private static readonly string[] ManagedClaimTypes =
    [
        AuthCenterBffDefaults.PermissionClaim,
        AuthCenterBffDefaults.ApplicationsClaim,
        "scope",
        "client_id",
        AuthCenterBffDefaults.RoleClaim,
        ClaimTypes.Role
    ];

    public static ClaimsPrincipal Enrich(
        ClaimsPrincipal principal,
        ClaimsPrincipal validatedAccessTokenPrincipal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(validatedAccessTokenPrincipal);

        var identity = principal.Identities.FirstOrDefault(item => item.IsAuthenticated)
            ?? throw new InvalidOperationException("An authenticated identity is required.");
        var subject = principal.FindFirstValue("sub")
            ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
        var tokenSubject = validatedAccessTokenPrincipal.FindFirstValue("sub")
            ?? validatedAccessTokenPrincipal.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!string.Equals(subject, tokenSubject, StringComparison.Ordinal))
            throw new InvalidOperationException("The ID token and access token subjects do not match.");

        foreach (var claim in identity.Claims.Where(item => ManagedClaimTypes.Contains(item.Type, StringComparer.Ordinal)).ToList())
            identity.RemoveClaim(claim);

        foreach (var claim in validatedAccessTokenPrincipal.Claims)
        {
            // Roles are stored under the identity's own role claim type so IsInRole and
            // [Authorize(Roles = ...)] work, whether AuthCenter sent "role" or the legacy .NET URI.
            if (AuthCenterRoleClaims.IsRoleClaim(claim.Type))
                identity.AddClaim(new Claim(identity.RoleClaimType, claim.Value, claim.ValueType, claim.Issuer));
            else if (ManagedClaimTypes.Contains(claim.Type, StringComparer.Ordinal))
                identity.AddClaim(new Claim(claim.Type, claim.Value, claim.ValueType, claim.Issuer));
        }

        return principal;
    }
}

internal static class AuthCenterRoleClaims
{
    public static bool IsRoleClaim(string claimType) =>
        string.Equals(claimType, AuthCenterBffDefaults.RoleClaim, StringComparison.Ordinal) ||
        string.Equals(claimType, ClaimTypes.Role, StringComparison.Ordinal);

    /// <summary>
    /// Adds a short <c>role</c> claim for every legacy URI role claim, for tokens issued before
    /// AuthCenter emitted the interoperable claim name.
    /// </summary>
    public static void NormalizeLegacyRoles(ClaimsIdentity identity)
    {
        foreach (var legacy in identity.FindAll(ClaimTypes.Role).ToList())
        {
            if (!identity.HasClaim(identity.RoleClaimType, legacy.Value))
                identity.AddClaim(new Claim(identity.RoleClaimType, legacy.Value, legacy.ValueType, legacy.Issuer));
        }
    }
}
