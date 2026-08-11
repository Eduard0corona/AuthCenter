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
            var claimType = claim.Type == AuthCenterBffDefaults.RoleClaim ? ClaimTypes.Role : claim.Type;
            if (ManagedClaimTypes.Contains(claimType, StringComparer.Ordinal))
                identity.AddClaim(new Claim(claimType, claim.Value, claim.ValueType, claim.Issuer));
        }

        return principal;
    }
}
