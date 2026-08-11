using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using AuthCenter.Infrastructure.Services;
using AuthCenter.Infrastructure.Settings;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace AuthCenter.UnitTests.Services;

public class ExternalIdentityTokenValidationTests
{
    [Fact]
    public async Task Microsoft_SingleTenant_ValidatesSignedToken()
    {
        using var rsa = RSA.Create(2048);
        var key = CreateSigningKey(rsa);
        var tenantId = Guid.NewGuid().ToString();
        const string clientId = "microsoft-client";
        var issuer = $"https://login.microsoftonline.com/{tenantId}/v2.0";
        var service = new MicrosoftAuthService(
            Options.Create(new MicrosoftAuthSettings { ClientId = clientId, TenantId = tenantId }),
            CreateConfigurationManager(issuer, key));
        var token = CreateToken(issuer, clientId, key,
            new Claim(JwtRegisteredClaimNames.Sub, "subject-123"),
            new Claim("tid", tenantId),
            new Claim("email", "user@example.com"),
            new Claim("name", "Example User"));

        var payload = await service.ValidateIdTokenAsync(token);

        Assert.NotNull(payload);
        Assert.Equal("subject-123", payload.Subject);
        Assert.Equal("user@example.com", payload.Email);
        Assert.Equal("Example User", payload.Name);
    }

    [Fact]
    public async Task Microsoft_MultiTenant_NamespacesSubjectWithValidatedTenant()
    {
        using var rsa = RSA.Create(2048);
        var key = CreateSigningKey(rsa);
        var tenantId = Guid.NewGuid().ToString();
        const string clientId = "microsoft-common-client";
        var issuer = $"https://login.microsoftonline.com/{tenantId}/v2.0";
        var service = new MicrosoftAuthService(
            Options.Create(new MicrosoftAuthSettings { ClientId = clientId, TenantId = "common" }),
            CreateConfigurationManager(issuer, key));
        var token = CreateToken(issuer, clientId, key,
            new Claim(JwtRegisteredClaimNames.Sub, "subject-456"),
            new Claim("tid", tenantId),
            new Claim("email", "tenant-user@example.com"));

        var payload = await service.ValidateIdTokenAsync(token);

        Assert.NotNull(payload);
        Assert.Equal($"{tenantId}:subject-456", payload.Subject);
    }

    [Fact]
    public async Task Apple_ValidatesSignedTokenWithVerifiedEmail()
    {
        using var rsa = RSA.Create(2048);
        var key = CreateSigningKey(rsa);
        const string issuer = "https://appleid.apple.com";
        const string clientId = "com.example.web";
        var service = new AppleAuthService(
            Options.Create(new AppleAuthSettings { ClientId = clientId }),
            CreateConfigurationManager(issuer, key));
        var token = CreateToken(issuer, clientId, key,
            new Claim(JwtRegisteredClaimNames.Sub, "apple-subject"),
            new Claim("email", "private-relay@example.com"),
            new Claim("email_verified", "true"),
            new Claim("name", "Apple User"));

        var payload = await service.ValidateIdTokenAsync(token);

        Assert.NotNull(payload);
        Assert.Equal("apple-subject", payload.Subject);
        Assert.Equal("private-relay@example.com", payload.Email);
    }

    [Fact]
    public async Task Apple_RejectsTokenWithUnverifiedEmail()
    {
        using var rsa = RSA.Create(2048);
        var key = CreateSigningKey(rsa);
        const string issuer = "https://appleid.apple.com";
        const string clientId = "com.example.web";
        var service = new AppleAuthService(
            Options.Create(new AppleAuthSettings { ClientId = clientId }),
            CreateConfigurationManager(issuer, key));
        var token = CreateToken(issuer, clientId, key,
            new Claim(JwtRegisteredClaimNames.Sub, "apple-subject"),
            new Claim("email", "unverified@example.com"),
            new Claim("email_verified", "false"));

        Assert.Null(await service.ValidateIdTokenAsync(token));
    }

    private static RsaSecurityKey CreateSigningKey(RSA rsa) => new(rsa) { KeyId = Guid.NewGuid().ToString("N") };

    private static IConfigurationManager<OpenIdConnectConfiguration> CreateConfigurationManager(
        string issuer,
        SecurityKey signingKey)
    {
        var configuration = new OpenIdConnectConfiguration { Issuer = issuer };
        configuration.SigningKeys.Add(signingKey);
        return new StaticConfigurationManager(configuration);
    }

    private static string CreateToken(string issuer, string audience, SecurityKey key, params Claim[] claims)
    {
        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken(
            issuer,
            audience,
            claims,
            now.AddMinutes(-1),
            now.AddMinutes(5),
            new SigningCredentials(key, SecurityAlgorithms.RsaSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private sealed class StaticConfigurationManager(OpenIdConnectConfiguration configuration)
        : IConfigurationManager<OpenIdConnectConfiguration>
    {
        public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel) =>
            Task.FromResult(configuration);

        public void RequestRefresh()
        {
        }
    }
}
