using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AuthCenter.Application.Interfaces;
using AuthCenter.Application.Models;
using AuthCenter.Domain.Constants;
using AuthCenter.Domain.Entities;
using AuthCenter.Infrastructure.Security;
using AuthCenter.Infrastructure.Settings;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace AuthCenter.Infrastructure.Services;

public class TokenService : ITokenService
{
    private readonly JwtSettings _jwtSettings;
    private readonly MfaSettings _mfaSettings;
    private readonly IDateTimeProvider _dateTimeProvider;
    private readonly RsaSigningKeyRing _keyRing;

    public TokenService(
        IOptions<JwtSettings> jwtSettings,
        IOptions<MfaSettings> mfaSettings,
        IDateTimeProvider dateTimeProvider,
        RsaSigningKeyRing keyRing)
    {
        _jwtSettings = jwtSettings.Value;
        _mfaSettings = mfaSettings.Value;
        _dateTimeProvider = dateTimeProvider;
        _keyRing = keyRing;
    }

    public int AccessTokenExpiryMinutes => _jwtSettings.AccessTokenMinutes;
    public int MagicLinkTokenMinutes => _jwtSettings.MagicLinkTokenMinutes;
    public bool IsRsaConfigured => _keyRing.IsConfigured;

    public string GenerateAccessToken(
        ApplicationUser user,
        IList<string> roles,
        IList<string> permissions,
        IList<string> applications,
        Guid? sessionId = null)
    {
        var creds = GetRsaSigningCredentials();

        var now = _dateTimeProvider.UtcNow;
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email!),
            new(JwtRegisteredClaimNames.Name, user.FullName),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            IssuedAt(now)
        };

        if (sessionId.HasValue)
            claims.Add(new Claim(JwtRegisteredClaimNames.Sid, sessionId.Value.ToString()));

        foreach (var role in roles)
            claims.Add(new Claim(DomainConstants.Claims.Role, role));

        foreach (var permission in permissions)
            claims.Add(new Claim(DomainConstants.Claims.Permissions, permission));

        foreach (var app in applications)
            claims.Add(new Claim(DomainConstants.Claims.Applications, app));

        return WriteAccessToken(creds, _jwtSettings.Audience, claims, now.AddMinutes(_jwtSettings.AccessTokenMinutes));
    }

    public string GenerateOAuthAccessToken(
        ApplicationUser? user,
        string clientId,
        string applicationCode,
        IList<string> scopes,
        IList<string> roles,
        IList<string> permissions,
        int lifetimeSeconds,
        TokenAuthentication? authentication = null,
        IReadOnlyList<string>? audiences = null,
        string? actorJson = null)
    {
        var creds = GetRsaSigningCredentials();
        var now = _dateTimeProvider.UtcNow;

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            IssuedAt(now),
            new("client_id", clientId),
            new("scope", string.Join(" ", scopes)),
            new(DomainConstants.Claims.Applications, applicationCode)
        };

        if (user is not null)
        {
            claims.Add(new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()));

            if (scopes.Contains(DomainConstants.OAuthScopes.Email))
                claims.Add(new Claim(JwtRegisteredClaimNames.Email, user.Email!));

            if (scopes.Contains(DomainConstants.OAuthScopes.Profile))
                claims.Add(new Claim(JwtRegisteredClaimNames.Name, user.FullName));

            foreach (var role in roles)
                claims.Add(new Claim(DomainConstants.Claims.Role, role));

            foreach (var permission in permissions)
                claims.Add(new Claim(DomainConstants.Claims.Permissions, permission));

            // RFC 9068 section 2.2.1: resource servers can require a recent or stronger authentication.
            if (authentication is not null)
            {
                claims.Add(new Claim("auth_time", ToUnixTime(authentication.AuthenticatedAt), ClaimValueTypes.Integer64));
                claims.Add(new Claim("acr", AuthenticationContext.ContextClass(authentication.Assurance)));
                // The session lets introspection report the token inactive once the user signs out.
                if (authentication.SessionId.HasValue)
                    claims.Add(new Claim(JwtRegisteredClaimNames.Sid, authentication.SessionId.Value.ToString()));
            }
        }

        // RFC 8693 section 4.1: the party acting on behalf of the subject.
        if (actorJson is not null)
            claims.Add(new Claim("act", actorJson, JsonClaimValueTypes.Json));

        return WriteAccessToken(creds, audiences is { Count: > 0 } ? audiences : [clientId], claims, now.AddSeconds(lifetimeSeconds));
    }

    public string? GenerateIdToken(ApplicationUser user, string clientId, string? nonce, IList<string> scopes, TokenAuthentication? authentication = null)
    {
        if (!_keyRing.IsConfigured) return null;

        var now = _dateTimeProvider.UtcNow;
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Iat, ToUnixTime(now), ClaimValueTypes.Integer64),
            // auth_time is when the user authenticated, which an SSO session can predate by hours.
            new("auth_time", ToUnixTime(authentication?.AuthenticatedAt ?? now), ClaimValueTypes.Integer64),
        };

        if (authentication is not null)
        {
            // amr is always a JSON array, even with a single method (OIDC Core section 2).
            claims.Add(new Claim("amr", JsonSerializer.Serialize(authentication.Methods), JsonClaimValueTypes.JsonArray));
            claims.Add(new Claim("acr", AuthenticationContext.ContextClass(authentication.Assurance)));
            if (authentication.SessionId.HasValue)
                claims.Add(new Claim(JwtRegisteredClaimNames.Sid, authentication.SessionId.Value.ToString()));
        }

        if (nonce is not null)
            claims.Add(new Claim("nonce", nonce));

        claims.Add(new Claim(JwtRegisteredClaimNames.Azp, clientId));

        if (scopes.Contains(DomainConstants.OAuthScopes.Email))
        {
            claims.Add(new Claim(JwtRegisteredClaimNames.Email, user.Email!));
            claims.Add(new Claim("email_verified", user.EmailConfirmed ? "true" : "false", ClaimValueTypes.Boolean));
        }

        if (scopes.Contains(DomainConstants.OAuthScopes.Profile))
            claims.Add(new Claim(JwtRegisteredClaimNames.Name, user.FullName));

        var creds = GetRsaSigningCredentials();

        var token = new JwtSecurityToken(
            issuer: _jwtSettings.Issuer,
            audience: clientId,
            claims: claims,
            expires: now.AddMinutes(5),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public string GetJwks() => _keyRing.Jwks;

    // Access tokens carry typ "at+jwt" (RFC 9068) so a resource server can refuse an ID token that
    // shares the same issuer, audience and algorithm.
    private string WriteAccessToken(SigningCredentials credentials, string audience, IEnumerable<Claim> claims, DateTime expires) =>
        WriteAccessToken(credentials, [audience], claims, expires);

    private string WriteAccessToken(SigningCredentials credentials, IReadOnlyList<string> audiences, IEnumerable<Claim> claims, DateTime expires)
    {
        var header = new JwtHeader(credentials, null, DomainConstants.Claims.AccessTokenType);
        var payload = new JwtPayload(_jwtSettings.Issuer, audiences.Count == 1 ? audiences[0] : null, claims, notBefore: null, expires: expires);
        if (audiences.Count > 1)
            payload[JwtRegisteredClaimNames.Aud] = audiences.ToArray();
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(header, payload));
    }

    private static Claim IssuedAt(DateTime now) =>
        new(JwtRegisteredClaimNames.Iat, ToUnixTime(now), ClaimValueTypes.Integer64);

    private static string ToUnixTime(DateTime value) =>
        new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);

    public string? ReadIdTokenHintSubject(string idToken, string clientId)
    {
        var hint = ReadIdTokenHint(idToken);
        return hint is not null && string.Equals(hint.ClientId, clientId, StringComparison.Ordinal) ? hint.Subject : null;
    }

    public IdTokenHint? ReadIdTokenHint(string idToken)
    {
        if (string.IsNullOrWhiteSpace(idToken) || !_keyRing.IsConfigured)
            return null;

        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        try
        {
            var principal = handler.ValidateToken(idToken, new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = _jwtSettings.Issuer,
                // The audience names the client; callers check it against the client they expect.
                ValidateAudience = false,
                // An id_token_hint may be expired; only its origin and subject matter.
                ValidateLifetime = false,
                ValidateIssuerSigningKey = true,
                IssuerSigningKeys = _keyRing.ValidationKeys,
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256]
            }, out var validated);

            // Access and logout tokens share issuer and keys but are not ID tokens.
            var type = ((JwtSecurityToken)validated).Header.Typ;
            if (string.Equals(type, DomainConstants.Claims.AccessTokenType, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(type, DomainConstants.Claims.LogoutTokenType, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var subject = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
            var audiences = principal.FindAll(JwtRegisteredClaimNames.Aud).Select(claim => claim.Value).ToList();
            if (string.IsNullOrEmpty(subject) || audiences.Count != 1)
                return null;
            return new IdTokenHint(
                subject,
                audiences[0],
                Guid.TryParse(principal.FindFirst(JwtRegisteredClaimNames.Sid)?.Value, out var sessionId) ? sessionId : null);
        }
        catch (Exception exception) when (exception is SecurityTokenException or ArgumentException)
        {
            return null;
        }
    }

    public ValidatedAccessToken? ValidateAccessToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 16_384 || !_keyRing.IsConfigured)
            return null;
        try
        {
            var principal = new JwtSecurityTokenHandler { MapInboundClaims = false }.ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = _jwtSettings.Issuer,
                ValidateAudience = false,
                ValidateLifetime = true,
                RequireExpirationTime = true,
                ClockSkew = TimeSpan.Zero,
                ValidateIssuerSigningKey = true,
                IssuerSigningKeys = _keyRing.ValidationKeys,
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                ValidTypes = [DomainConstants.Claims.AccessTokenType]
            }, out var validated);
            var jwt = (JwtSecurityToken)validated;
            return new ValidatedAccessToken(
                principal,
                jwt.Audiences.ToList(),
                jwt.ValidTo,
                jwt.IssuedAt == DateTime.MinValue ? null : jwt.IssuedAt);
        }
        catch (Exception exception) when (exception is SecurityTokenException or ArgumentException)
        {
            return null;
        }
    }

    public string GenerateLogoutToken(string clientId, Guid userId, Guid? sessionId)
    {
        var credentials = GetRsaSigningCredentials();
        var now = _dateTimeProvider.UtcNow;
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId.ToString()),
            IssuedAt(now),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new("events", JsonSerializer.Serialize(new Dictionary<string, object> { [DomainConstants.Claims.BackchannelLogoutEvent] = new { } }), JsonClaimValueTypes.Json)
        };
        if (sessionId.HasValue)
            claims.Add(new Claim(JwtRegisteredClaimNames.Sid, sessionId.Value.ToString()));

        // Logout tokens never carry a nonce and use their own type, so they cannot be replayed as
        // ID or access tokens (OpenID Connect Back-Channel Logout 1.0, section 2.4).
        var header = new JwtHeader(credentials, null, DomainConstants.Claims.LogoutTokenType);
        var payload = new JwtPayload(_jwtSettings.Issuer, clientId, claims, notBefore: null, expires: now.AddMinutes(2));
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(header, payload));
    }

    private SigningCredentials GetRsaSigningCredentials() => _keyRing.RequireSigningCredentials();

    public (string token, string hash) GenerateRefreshToken()
    {
        var randomBytes = new byte[64];
        RandomNumberGenerator.Fill(randomBytes);
        var token = Convert.ToBase64String(randomBytes);
        return (token, HashToken(token));
    }

    public string GenerateMfaPendingToken(Guid userId, string applicationCode, string? primaryMethod = null)
    {
        return GeneratePendingToken(userId, applicationCode, "mfa_pending", _mfaSettings.MfaTokenExpirySeconds, primaryMethod);
    }

    public MfaPendingTokenValidationResult? ValidateMfaPendingToken(string token)
    {
        return ValidatePendingToken(token, "mfa_pending");
    }

    public string GenerateForcedChangePendingToken(Guid userId, string applicationCode)
    {
        return GeneratePendingToken(userId, applicationCode, "forced_change");
    }

    public MfaPendingTokenValidationResult? ValidateForcedChangePendingToken(string token)
    {
        return ValidatePendingToken(token, "forced_change");
    }

    public string GenerateMagicLinkToken(Guid userId, string applicationCode)
    {
        return GeneratePendingToken(userId, applicationCode, "magic_link", _jwtSettings.MagicLinkTokenMinutes * 60.0);
    }

    public MfaPendingTokenValidationResult? ValidateMagicLinkToken(string token)
    {
        return ValidatePendingToken(token, "magic_link");
    }

    public string HashToken(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToBase64String(bytes);
    }

    private string GeneratePendingToken(Guid userId, string applicationCode, string purpose)
    {
        return GeneratePendingToken(userId, applicationCode, purpose, _mfaSettings.MfaTokenExpirySeconds);
    }

    private string GeneratePendingToken(Guid userId, string applicationCode, string purpose, double expirySeconds, string? primaryMethod = null)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new("appCode", applicationCode),
            new("purpose", purpose)
        };
        if (!string.IsNullOrWhiteSpace(primaryMethod))
            claims.Add(new Claim("amr", primaryMethod));

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtSettings.SigningKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _jwtSettings.Issuer,
            audience: _jwtSettings.Audience,
            claims: claims,
            expires: _dateTimeProvider.UtcNow.AddSeconds(expirySeconds),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private MfaPendingTokenValidationResult? ValidatePendingToken(string token, string purpose)
    {
        var handler = new JwtSecurityTokenHandler();
        handler.InboundClaimTypeMap.Clear();

        try
        {
            var principal = handler.ValidateToken(token, new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = _jwtSettings.Issuer,
                ValidAudience = _jwtSettings.Audience,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtSettings.SigningKey)),
                ClockSkew = TimeSpan.Zero,
                ValidAlgorithms = [SecurityAlgorithms.HmacSha256]
            }, out _);

            if (principal.FindFirst("purpose")?.Value != purpose)
                return null;

            var subject = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
            var applicationCode = principal.FindFirst("appCode")?.Value;
            var tokenId = principal.FindFirst(JwtRegisteredClaimNames.Jti)?.Value;

            if (!Guid.TryParse(subject, out var userId) ||
                string.IsNullOrWhiteSpace(applicationCode) ||
                string.IsNullOrWhiteSpace(tokenId))
            {
                return null;
            }

            return new MfaPendingTokenValidationResult(userId, applicationCode, tokenId, principal.FindFirst("amr")?.Value);
        }
        catch
        {
            return null;
        }
    }
}
