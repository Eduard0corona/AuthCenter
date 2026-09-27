using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using AuthCenter.Application.Models;
using AuthCenter.Domain.Constants;
using AuthCenter.Domain.Entities;
using AuthCenter.Domain.Enums;
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
            SigningKey = TestSecretGenerator.CreateKey(),
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
            tokenService.GenerateOAuthAccessToken(null, "test-client", "TEST_APP", ["email"], [], [], 300));

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

    [Fact]
    public void OAuthAccessAndIdTokens_CarryTheRecordedAmrAsAJsonArray()
    {
        var user = TestUser();
        var authentication = new TokenAuthentication(
            DateTime.UtcNow.AddMinutes(-5),
            AuthenticationContext.WithSecondFactor(DomainConstants.AuthenticationMethods.Password).Methods,
            AuthenticationAssuranceLevel.Mfa,
            Guid.NewGuid());

        var accessToken = ReadPayload(_tokenService.GenerateOAuthAccessToken(user, "client", "APP", ["openid"], [], [], 300, authentication));
        var idToken = ReadPayload(_tokenService.GenerateIdToken(user, "client", "nonce", ["openid"], authentication)!);

        string[] expected = ["pwd", "otp", "mfa"];
        foreach (var payload in new[] { accessToken, idToken })
        {
            Assert.Equal(expected, AmrArray(payload));
            Assert.Equal(DomainConstants.AuthenticationContextClasses.MultiFactor, payload["acr"]);
        }
    }

    [Fact]
    public void OAuthAccessToken_SinglePasswordMethod_IsStillAnArray_AndNoAuthenticationMeansNoAmr()
    {
        var user = TestUser();
        var password = new TokenAuthentication(DateTime.UtcNow, AuthenticationContext.Password.Methods, AuthenticationAssuranceLevel.Password, null);

        var withPassword = ReadPayload(_tokenService.GenerateOAuthAccessToken(user, "client", "APP", ["openid"], [], [], 300, password));
        var withoutAuthentication = ReadPayload(_tokenService.GenerateOAuthAccessToken(user, "client", "APP", ["openid"], [], [], 300));
        var withoutMethods = ReadPayload(_tokenService.GenerateOAuthAccessToken(
            user, "client", "APP", ["openid"], [], [], 300, password with { Methods = [] }));

        Assert.Equal(["pwd"], AmrArray(withPassword));
        Assert.False(withoutAuthentication.ContainsKey("amr"));
        Assert.False(withoutMethods.ContainsKey("amr"));
    }

    private static ApplicationUser TestUser() => new()
    {
        Id = Guid.NewGuid(),
        FullName = "Test User",
        Email = "test@example.com",
        UserName = "test@example.com",
        IsActive = true,
        CreatedAt = DateTime.UtcNow
    };

    private static JwtPayload ReadPayload(string token) => new JwtSecurityTokenHandler().ReadJwtToken(token).Payload;

    /// <summary>The amr member of the serialized payload, which must be a JSON array of strings.</summary>
    private static string[] AmrArray(JwtPayload payload)
    {
        using var json = System.Text.Json.JsonDocument.Parse(payload.SerializeToJson());
        var amr = json.RootElement.GetProperty("amr");
        Assert.Equal(System.Text.Json.JsonValueKind.Array, amr.ValueKind);
        return amr.EnumerateArray().Select(value => value.GetString()!).ToArray();
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
            Options.Create(new MfaSettings { EncryptionKey = TestSecretGenerator.CreateKey(), MfaTokenExpirySeconds = 300 }),
            new DateTimeProvider(),
            keyRing);
    }

    private static TokenService CreateTokenServiceWithoutRsa()
    {
        var settings = new JwtSettings
        {
            Issuer = "TestIssuer",
            Audience = "TestAudience",
            SigningKey = TestSecretGenerator.CreateKey()
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
