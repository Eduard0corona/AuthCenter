using AuthCenter.Infrastructure.Security;
using AuthCenter.Infrastructure.Services;
using AuthCenter.Infrastructure.Settings;
using Microsoft.Extensions.Options;
using Xunit;

namespace AuthCenter.UnitTests.Services;

public class RefreshTokenHashTests
{
    private readonly TokenService _tokenService;

    public RefreshTokenHashTests()
    {
        var settings = Options.Create(new JwtSettings
        {
            Issuer = "TestIssuer",
            Audience = "TestAudience",
            SigningKey = "test-signing-key-that-is-long-enough-32chars",
            AccessTokenMinutes = 15,
            RefreshTokenDays = 30
        });
        var dateTimeProvider = new DateTimeProvider();
        _tokenService = new TokenService(
            settings,
            Options.Create(new MfaSettings { EncryptionKey = "test-mfa-key", MfaTokenExpirySeconds = 300 }),
            dateTimeProvider,
            new RsaSigningKeyRing(settings));
    }

    [Fact]
    public void HashToken_ProducesNonEmptyConsistentHash()
    {
        const string token = "some-refresh-token-value";
        var hash1 = _tokenService.HashToken(token);
        var hash2 = _tokenService.HashToken(token);

        Assert.NotEmpty(hash1);
        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void HashToken_DifferentTokensProduceDifferentHashes()
    {
        var hash1 = _tokenService.HashToken("token-a");
        var hash2 = _tokenService.HashToken("token-b");

        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void GenerateRefreshToken_TokenIsLongEnough()
    {
        var (token, hash) = _tokenService.GenerateRefreshToken();

        Assert.True(token.Length >= 40, $"Token length {token.Length} is too short");
        Assert.NotEmpty(hash);
        Assert.NotEqual(token, hash);
    }

    [Fact]
    public void GenerateRefreshToken_HashMatchesToken()
    {
        var (token, hash) = _tokenService.GenerateRefreshToken();
        var recomputed = _tokenService.HashToken(token);

        Assert.Equal(hash, recomputed);
    }
}
