using System.IdentityModel.Tokens.Jwt;
using System.Text;
using AuthCenter.Domain.Entities;
using AuthCenter.Infrastructure.Services;
using AuthCenter.Infrastructure.Settings;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace AuthCenter.UnitTests.Services;

public class TokenServiceTests
{
    private readonly TokenService _tokenService;
    private readonly JwtSettings _settings;

    public TokenServiceTests()
    {
        _settings = new JwtSettings
        {
            Issuer = "TestIssuer",
            Audience = "TestAudience",
            SigningKey = "test-signing-key-that-is-long-enough-32chars",
            AccessTokenMinutes = 15,
            RefreshTokenDays = 30,
            MagicLinkTokenMinutes = 15
        };
        _tokenService = new TokenService(
            Options.Create(_settings),
            Options.Create(new MfaSettings { EncryptionKey = "test-mfa-key", MfaTokenExpirySeconds = 300 }),
            new DateTimeProvider());
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
        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = _settings.Issuer,
            ValidAudience = _settings.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_settings.SigningKey)),
            ClockSkew = TimeSpan.Zero
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
}
