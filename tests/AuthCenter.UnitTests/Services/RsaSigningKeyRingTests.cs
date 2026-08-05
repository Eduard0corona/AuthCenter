using System.Security.Cryptography;
using System.Text.Json;
using AuthCenter.Infrastructure.Security;
using AuthCenter.Infrastructure.Settings;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace AuthCenter.UnitTests.Services;

public class RsaSigningKeyRingTests
{
    [Fact]
    public void SigningKeyId_IsDerivedFromTheKeyItself()
    {
        using var rsa = RSA.Create(2048);
        using var keyRing = CreateKeyRing(rsa.ExportPkcs8PrivateKeyPem());
        using var sameKeyAgain = CreateKeyRing(rsa.ExportPkcs8PrivateKeyPem());

        using var otherRsa = RSA.Create(2048);
        using var otherKeyRing = CreateKeyRing(otherRsa.ExportPkcs8PrivateKeyPem());

        var keyId = keyRing.SigningCredentials!.Key.KeyId;

        Assert.False(string.IsNullOrWhiteSpace(keyId));
        Assert.Equal(keyId, sameKeyAgain.SigningCredentials!.Key.KeyId);
        Assert.NotEqual(keyId, otherKeyRing.SigningCredentials!.Key.KeyId);
    }

    [Fact]
    public void RetiredKeys_AreStillAcceptedForValidationAndPublished()
    {
        using var retiredRsa = RSA.Create(2048);
        using var activeRsa = RSA.Create(2048);

        // A token signed before the rotation.
        using var beforeRotation = CreateKeyRing(retiredRsa.ExportPkcs8PrivateKeyPem());
        var retiredKeyId = beforeRotation.SigningCredentials!.Key.KeyId;

        // After promoting a new key, the old one moves to the additional slot.
        using var afterRotation = CreateKeyRing(
            activeRsa.ExportPkcs8PrivateKeyPem(),
            retiredRsa.ExportSubjectPublicKeyInfoPem());

        Assert.NotEqual(retiredKeyId, afterRotation.SigningCredentials!.Key.KeyId);
        Assert.Contains(afterRotation.ValidationKeys, key => key.KeyId == retiredKeyId);
        Assert.Equal(2, afterRotation.ValidationKeys.Count);

        var publishedKeyIds = PublishedKeyIds(afterRotation);
        Assert.Equal(2, publishedKeyIds.Count);
        Assert.Contains(retiredKeyId, publishedKeyIds);
        Assert.Contains(afterRotation.SigningCredentials.Key.KeyId, publishedKeyIds);
    }

    [Fact]
    public async Task TokenSignedBeforeRotation_StillValidatesAfterIt()
    {
        using var retiredRsa = RSA.Create(2048);
        using var activeRsa = RSA.Create(2048);

        using var beforeRotation = CreateKeyRing(retiredRsa.ExportPkcs8PrivateKeyPem());
        var token = new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler().CreateToken(
            new SecurityTokenDescriptor
            {
                Issuer = "TestIssuer",
                Audience = "TestAudience",
                Expires = DateTime.UtcNow.AddMinutes(5),
                SigningCredentials = beforeRotation.SigningCredentials
            });

        using var afterRotation = CreateKeyRing(
            activeRsa.ExportPkcs8PrivateKeyPem(),
            retiredRsa.ExportSubjectPublicKeyInfoPem());

        var result = await new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler()
            .ValidateTokenAsync(token, new TokenValidationParameters
            {
                ValidIssuer = "TestIssuer",
                ValidAudience = "TestAudience",
                IssuerSigningKeys = afterRotation.ValidationKeys,
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                ClockSkew = TimeSpan.Zero
            });

        Assert.True(result.IsValid, result.Exception?.Message);
    }

    [Fact]
    public void PublishedKeys_NeverCarryThePrivateHalf()
    {
        using var rsa = RSA.Create(2048);
        using var keyRing = CreateKeyRing(rsa.ExportPkcs8PrivateKeyPem());

        foreach (var key in keyRing.ValidationKeys.Cast<RsaSecurityKey>())
        {
            Assert.Null(key.Parameters.D);
            Assert.Null(key.Parameters.P);
        }

        var jwks = JsonDocument.Parse(keyRing.Jwks).RootElement.GetProperty("keys");
        foreach (var jwk in jwks.EnumerateArray())
        {
            Assert.False(jwk.TryGetProperty("d", out _), "The JWKS must never expose private key material.");
            Assert.False(jwk.TryGetProperty("p", out _), "The JWKS must never expose private key material.");
        }
    }

    [Fact]
    public void DuplicatedKey_IsPublishedOnlyOnce()
    {
        using var rsa = RSA.Create(2048);
        using var keyRing = CreateKeyRing(rsa.ExportPkcs8PrivateKeyPem(), rsa.ExportSubjectPublicKeyInfoPem());

        Assert.Single(keyRing.ValidationKeys);
        Assert.Single(PublishedKeyIds(keyRing));
    }

    [Fact]
    public void KeyBelowMinimumSize_IsRejected()
    {
        using var weakRsa = RSA.Create(1024);

        var error = RsaSigningKeyRing.DescribeConfigurationError(new JwtSettings
        {
            RsaPrivateKeyPem = weakRsa.ExportPkcs8PrivateKeyPem()
        });

        Assert.NotNull(error);
        Assert.Contains("2048", error);
    }

    [Fact]
    public void InvalidAdditionalKey_IsReportedWithItsPosition()
    {
        using var rsa = RSA.Create(2048);

        var error = RsaSigningKeyRing.DescribeConfigurationError(new JwtSettings
        {
            RsaPrivateKeyPem = rsa.ExportPkcs8PrivateKeyPem(),
            AdditionalValidationKeysPem = ["not a pem key at all"]
        });

        Assert.NotNull(error);
        Assert.Contains("Jwt:AdditionalValidationKeysPem:0", error);
    }

    [Fact]
    public void PlaceholderKey_IsTreatedAsMissing()
    {
        var error = RsaSigningKeyRing.DescribeConfigurationError(new JwtSettings
        {
            RsaPrivateKeyPem = "REPLACE_WITH_RSA_PRIVATE_KEY_PEM"
        });

        Assert.NotNull(error);
        Assert.Contains("Jwt:RsaPrivateKeyPem", error);
    }

    [Fact]
    public void WithoutAnyKey_NothingCanBeSigned()
    {
        using var keyRing = CreateKeyRing(string.Empty);

        Assert.False(keyRing.IsConfigured);
        Assert.Empty(keyRing.ValidationKeys);
        Assert.Throws<InvalidOperationException>(() => keyRing.RequireSigningCredentials());
    }

    private static RsaSigningKeyRing CreateKeyRing(string signingKeyPem, params string[] additionalKeysPem)
    {
        return new RsaSigningKeyRing(Options.Create(new JwtSettings
        {
            Issuer = "TestIssuer",
            Audience = "TestAudience",
            RsaPrivateKeyPem = signingKeyPem,
            AdditionalValidationKeysPem = additionalKeysPem
        }));
    }

    private static List<string> PublishedKeyIds(RsaSigningKeyRing keyRing)
    {
        return JsonDocument.Parse(keyRing.Jwks).RootElement
            .GetProperty("keys")
            .EnumerateArray()
            .Select(key => key.GetProperty("kid").GetString()!)
            .ToList();
    }
}
