using AuthCenter.Domain.Entities;
using AuthCenter.Application.Models;

namespace AuthCenter.Application.Interfaces;

public interface ITokenService
{
    string GenerateAccessToken(
        ApplicationUser user,
        IList<string> roles,
        IList<string> permissions,
        IList<string> applications,
        Guid? sessionId = null);

    (string token, string hash) GenerateRefreshToken();

    string GenerateMfaPendingToken(Guid userId, string applicationCode);

    MfaPendingTokenValidationResult? ValidateMfaPendingToken(string token);

    string GenerateForcedChangePendingToken(Guid userId, string applicationCode);

    MfaPendingTokenValidationResult? ValidateForcedChangePendingToken(string token);

    string GenerateMagicLinkToken(Guid userId, string applicationCode);

    MfaPendingTokenValidationResult? ValidateMagicLinkToken(string token);

    string HashToken(string token);

    string GenerateOAuthAccessToken(
        ApplicationUser? user,
        string clientId,
        string applicationCode,
        IList<string> scopes,
        IList<string> roles,
        IList<string> permissions,
        int lifetimeSeconds);
    string? GenerateIdToken(ApplicationUser user, string clientId, string? nonce, IList<string> scopes);
    string GetJwks();

    int AccessTokenExpiryMinutes { get; }
    int MagicLinkTokenMinutes { get; }
    bool IsRsaConfigured { get; }
}
