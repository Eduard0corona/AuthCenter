using System.Security.Claims;

namespace AuthCenter.Application.Models;

/// <summary>An access token this server issued that passed signature, issuer, type and lifetime checks.</summary>
public sealed record ValidatedAccessToken(ClaimsPrincipal Principal, IReadOnlyList<string> Audiences, DateTime ExpiresAt, DateTime? IssuedAt)
{
    public string? ClientId => Principal.FindFirst("client_id")?.Value;
    public string? Subject => Principal.FindFirst("sub")?.Value;
    public string? SessionId => Principal.FindFirst("sid")?.Value;
    public string? Scope => Principal.FindFirst("scope")?.Value;
    public string? TokenId => Principal.FindFirst("jti")?.Value;
}
