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

    string GenerateMfaPendingToken(Guid userId, string applicationCode, string? primaryMethod = null);

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
        int lifetimeSeconds,
        TokenAuthentication? authentication = null,
        IReadOnlyList<string>? audiences = null,
        string? actorJson = null);

    string? GenerateIdToken(ApplicationUser user, string clientId, string? nonce, IList<string> scopes, TokenAuthentication? authentication = null);

    /// <summary>
    /// Returns the subject of an ID token this server issued to <paramref name="clientId"/>, even if
    /// it has expired (OIDC id_token_hint), or null when the token was not issued by this server.
    /// </summary>
    string? ReadIdTokenHintSubject(string idToken, string clientId);

    /// <summary>
    /// Reads an ID token this server issued, even if it has expired (RP-initiated logout), or
    /// returns null when it was not issued by this server or is not an ID token.
    /// </summary>
    IdTokenHint? ReadIdTokenHint(string idToken);

    /// <summary>An OpenID Connect back-channel logout token (typ logout+jwt) for one client.</summary>
    string GenerateLogoutToken(string clientId, Guid userId, Guid? sessionId);

    /// <summary>
    /// Validates an access token this server issued (signature, issuer, typ at+jwt and lifetime),
    /// or returns null. The audience is left to the caller, which knows who is asking.
    /// </summary>
    ValidatedAccessToken? ValidateAccessToken(string token);
    string GetJwks();

    int AccessTokenExpiryMinutes { get; }
    int MagicLinkTokenMinutes { get; }
    bool IsRsaConfigured { get; }
}
