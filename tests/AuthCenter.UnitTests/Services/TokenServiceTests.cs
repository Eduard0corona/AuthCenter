using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using AuthCenter.Domain.Entities;
using AuthCenter.Infrastructure.Security;
using AuthCenter.Infrastructure.Services;
using AuthCenter.Infrastructure.Settings;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace AuthCenter.UnitTests.Services;

public class TokenServiceTests : IDisposable
{
    private readonly TokenService _tokenService;
    private readonly JwtSettings _settings;
    private readonly RSA _validationRsa;
    private readonly RsaSigningKeyRing _keyRing;

    public TokenServiceTests()
    {
        _validationRsa = RSA.Create(2048);
        _settings = new JwtSettings
        {
            Issuer = "TestIssuer",
            Audience = "TestAudience",
            SigningKey = "test-signing-key-that-is-long-enough-32chars",
            AccessTokenMinutes = 15,
            RefreshTokenDays = 30,
            MagicLinkTokenMinutes = 15,
            RsaPrivateKeyPem = _validationRsa.ExportPkcs8PrivateKeyPem()
        };
        _keyRing = new RsaSigningKeyRing(Options.Create(_settings));
        _tokenService = CreateTokenService(_settings, _keyRing);
    }

    [Fact]
    public void GenerateAccessToken_ReturnsValidJwt()
    {
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            FullName = "Test User",
            Email = "test@example.com",
            UserName = "test@example.com",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        var token = _tokenService.GenerateAccessToken(user, ["Admin"], ["AUTHCENTER_USERS_READ"], ["AUTHCENTER"]);

        Assert.NotEmpty(token);

        var handler = new JwtSecurityTokenHandler();
        handler.InboundClaimTypeMap.Clear(); // prevent sub → NameIdentifier remapping
        var jwt = handler.ReadJwtToken(token);
        Assert.Equal(SecurityAlgorithms.RsaSha256, jwt.Header.Alg);
        Assert.Equal(_keyRing.SigningCredentials!.Key.KeyId, jwt.Header.Kid);

        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = _settings.Issuer,
            ValidAudience = _settings.Audience,
            IssuerSigningKeys = _keyRing.ValidationKeys,
            ClockSkew = TimeSpan.Zero,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256]
        };

        var principal = handler.ValidateToken(token, parameters, out _);

        Assert.NotNull(principal);
        Assert.Equal(user.Id.ToString(), principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value);
        Assert.Equal(user.Email, principal.FindFirst(JwtRegisteredClaimNames.Email)?.Value);
    }

    [Fact]
    public void AccessTokenExpiryMinutes_ReturnsConfiguredValue()
    {
        Assert.Equal(15, _tokenService.AccessTokenExpiryMinutes);
    }

    [Fact]
    public void GenerateAccessToken_WithoutRsa_ThrowsExplicitly()
    {
        var tokenService = CreateTokenServiceWithoutRsa();
        var user = CreateUser();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            tokenService.GenerateAccessToken(user, [], [], []));

        Assert.Contains("Jwt:RsaPrivateKeyPem", exception.Message);
    }

    [Fact]
    public void GenerateOAuthAccessToken_WithoutRsa_ThrowsExplicitly()
    {
        var tokenService = CreateTokenServiceWithoutRsa();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            tokenService.GenerateOAuthAccessToken(null, "test-client", ["email"], 300));

        Assert.Contains("Jwt:RsaPrivateKeyPem", exception.Message);
    }

    [Fact]
    public void MfaPendingToken_ValidatesPurposeUserApplicationAndTokenId()
    {
        var userId = Guid.NewGuid();

        var token = _tokenService.GenerateMfaPendingToken(userId, "AUTHCENTER");
        var result = _tokenService.ValidateMfaPendingToken(token);

        Assert.NotNull(result);
        Assert.Equal(userId, result.UserId);
        Assert.Equal("AUTHCENTER", result.ApplicationCode);
        Assert.False(string.IsNullOrWhiteSpace(result.TokenId));
    }

    [Fact]
    public void ForcedChangePendingToken_ValidatesPurposeUserApplicationAndTokenId()
    {
        var userId = Guid.NewGuid();

        var token = _tokenService.GenerateForcedChangePendingToken(userId, "AUTHCENTER");
        var result = _tokenService.ValidateForcedChangePendingToken(token);

        Assert.NotNull(result);
        Assert.Equal(userId, result.UserId);
        Assert.Equal("AUTHCENTER", result.ApplicationCode);
        Assert.False(string.IsNullOrWhiteSpace(result.TokenId));
    }

    [Fact]
    public void MagicLinkToken_ValidatesPurposeUserAndApp()
    {
        var userId = Guid.NewGuid();

        var token = _tokenService.GenerateMagicLinkToken(userId, "AUTHCENTER");
        var result = _tokenService.ValidateMagicLinkToken(token);

        Assert.NotNull(result);
        Assert.Equal(userId, result.UserId);
        Assert.Equal("AUTHCENTER", result.ApplicationCode);
        Assert.False(string.IsNullOrWhiteSpace(result.TokenId));
    }

    [Fact]
    public void MagicLinkToken_RejectsWrongPurpose()
    {
        var userId = Guid.NewGuid();

        var token = _tokenService.GenerateMfaPendingToken(userId, "AUTHCENTER");
        var result = _tokenService.ValidateMagicLinkToken(token);

        Assert.Null(result);
    }

    public void Dispose()
    {
        _keyRing.Dispose();
        _validationRsa.Dispose();
        GC.SuppressFinalize(this);
    }

    private static TokenService CreateTokenService(JwtSettings settings, RsaSigningKeyRing keyRing)
    {
        return new TokenService(
            Options.Create(settings),
            Options.Create(new MfaSettings { EncryptionKey = "test-mfa-key", MfaTokenExpirySeconds = 300 }),
            new DateTimeProvider(),
            keyRing);
    }

    private static TokenService CreateTokenServiceWithoutRsa()
    {
        var settings = new JwtSettings
        {
            Issuer = "TestIssuer",
            Audience = "TestAudience",
            SigningKey = "test-signing-key-that-is-long-enough-32chars"
        };

        return CreateTokenService(settings, new RsaSigningKeyRing(Options.Create(settings)));
    }

    private static ApplicationUser CreateUser()
    {
        return new ApplicationUser
        {
            Id = Guid.NewGuid(),
            FullName = "Test User",
            Email = "test@example.com",
            UserName = "test@example.com",
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
    }
}
