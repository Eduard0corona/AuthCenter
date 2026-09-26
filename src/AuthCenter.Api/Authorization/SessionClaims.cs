using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace AuthCenter.Api.Authorization;

/// <summary>
/// Reads the user and single sign-on session identifiers from either a hosted-login cookie
/// principal (raw JWT claim names) or a bearer principal (claim names may be mapped).
/// </summary>
public static class SessionClaims
{
    public static Guid? UserId(ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(JwtRegisteredClaimNames.Sub) ?? principal.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            ? id
            : null;

    public static Guid? SessionId(ClaimsPrincipal principal) =>
        Guid.TryParse(principal.FindFirstValue(JwtRegisteredClaimNames.Sid) ?? principal.FindFirstValue(ClaimTypes.Sid), out var id)
            ? id
            : null;
}
