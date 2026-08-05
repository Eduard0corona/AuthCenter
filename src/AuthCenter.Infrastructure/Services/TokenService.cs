using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
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
        IList<string> applications)
    {
        var creds = GetRsaSigningCredentials();

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email!),
            new(JwtRegisteredClaimNames.Name, user.FullName),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };

        foreach (var role in roles)
            claims.Add(new Claim(ClaimTypes.Role, role));

        foreach (var permission in permissions)
            claims.Add(new Claim(DomainConstants.Claims.Permissions, permission));

        foreach (var app in applications)
            claims.Add(new Claim(DomainConstants.Claims.Applications, app));

        var token = new JwtSecurityToken(
            issuer: _jwtSettings.Issuer,
            audience: _jwtSettings.Audience,
            claims: claims,
            expires: _dateTimeProvider.UtcNow.AddMinutes(_jwtSettings.AccessTokenMinutes),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public string GenerateOAuthAccessToken(ApplicationUser? user, string clientId, IList<string> scopes, int lifetimeSeconds)
    {
        var creds = GetRsaSigningCredentials();

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new("client_id", clientId),
            new("scope", string.Join(" ", scopes))
        };

        if (user is not null)
        {
            claims.Add(new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()));
            claims.Add(new Claim(JwtRegisteredClaimNames.Email, user.Email!));
        }

        var token = new JwtSecurityToken(
            issuer: _jwtSettings.Issuer,
            audience: clientId,
            claims: claims,
            expires: _dateTimeProvider.UtcNow.AddSeconds(lifetimeSeconds),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public string? GenerateIdToken(ApplicationUser user, string clientId, string? nonce, IList<string> scopes)
    {
        if (!_keyRing.IsConfigured) return null;

        var now = _dateTimeProvider.UtcNow;
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Iat, new DateTimeOffset(now).ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
            new("auth_time", new DateTimeOffset(now).ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64),
        };

        if (nonce is not null)
            claims.Add(new Claim("nonce", nonce));

        if (scopes.Contains(DomainConstants.OAuthScopes.Email))
        {
            claims.Add(new Claim(JwtRegisteredClaimNames.Email, user.Email!));
            claims.Add(new Claim("email_verified", user.EmailConfirmed.ToString().ToLowerInvariant()));
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

    private SigningCredentials GetRsaSigningCredentials() => _keyRing.RequireSigningCredentials();

    public (string token, string hash) GenerateRefreshToken()
    {
        var randomBytes = new byte[64];
        RandomNumberGenerator.Fill(randomBytes);
        var token = Convert.ToBase64String(randomBytes);
        return (token, HashToken(token));
    }

    public string GenerateMfaPendingToken(Guid userId, string applicationCode)
    {
        return GeneratePendingToken(userId, applicationCode, "mfa_pending");
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

    private string GeneratePendingToken(Guid userId, string applicationCode, string purpose, double expirySeconds)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new("appCode", applicationCode),
            new("purpose", purpose)
        };

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

            return new MfaPendingTokenValidationResult(userId, applicationCode, tokenId);
        }
        catch
        {
            return null;
        }
    }
}
