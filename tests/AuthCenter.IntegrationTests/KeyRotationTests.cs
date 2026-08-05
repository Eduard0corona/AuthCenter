using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Responses.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace AuthCenter.IntegrationTests;

/// <summary>
/// Covers the rotation contract end to end: while a key sits in
/// <c>Jwt:AdditionalValidationKeysPem</c>, tokens it signed keep working and relying parties can
/// still discover it through the JWKS, even though new tokens are signed with the active key.
/// </summary>
public class KeyRotationTests
{
    [Fact]
    public async Task TokenSignedWithARetiredKey_IsStillAccepted()
    {
        using var retiredRsa = RSA.Create(2048);
        using var factory = CreateFactoryWithRetiredKey(retiredRsa);
        using var client = factory.CreateClient();

        var activeToken = await GetAdminTokenAsync(client);
        var tokenSignedWithRetiredKey = ReSignWithRetiredKey(activeToken, retiredRsa);

        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", tokenSignedWithRetiredKey);

        var response = await client.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task TokenSignedWithAnUnknownKey_IsRejected()
    {
        using var retiredRsa = RSA.Create(2048);
        using var strangerRsa = RSA.Create(2048);
        using var factory = CreateFactoryWithRetiredKey(retiredRsa);
        using var client = factory.CreateClient();

        var activeToken = await GetAdminTokenAsync(client);
        var forgedToken = ReSignWithRetiredKey(activeToken, strangerRsa);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", forgedToken);

        var response = await client.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Jwks_PublishesBothTheActiveAndTheRetiredKey()
    {
        using var retiredRsa = RSA.Create(2048);
        using var factory = CreateFactoryWithRetiredKey(retiredRsa);
        using var client = factory.CreateClient();

        var activeToken = await GetAdminTokenAsync(client);
        var activeKeyId = new JwtSecurityTokenHandler().ReadJwtToken(activeToken).Header.Kid;

        var jwks = await client.GetFromJsonAsync<JsonElement>("/.well-known/jwks.json");
        var keys = jwks.GetProperty("keys").EnumerateArray().ToList();

        Assert.Equal(2, keys.Count);
        Assert.Contains(keys, key => key.GetProperty("kid").GetString() == activeKeyId);
        Assert.All(keys, key => Assert.Equal("RS256", key.GetProperty("alg").GetString()));
        // Only the active key signs, but both must be discoverable while the rotation is in flight.
        Assert.Equal(2, keys.Select(key => key.GetProperty("kid").GetString()).Distinct().Count());
    }

    private static WebApplicationFactory<Program> CreateFactoryWithRetiredKey(RSA retiredRsa)
    {
        return new AuthCenterWebApplicationFactory().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Jwt:AdditionalValidationKeysPem:0"] = retiredRsa.ExportSubjectPublicKeyInfoPem()
                })));
    }

    /// <summary>
    /// Rebuilds a token the server just issued, signing it with a different key and leaving every
    /// claim untouched, so the only thing under test is which key the signature came from.
    /// </summary>
    private static string ReSignWithRetiredKey(string originalToken, RSA signingRsa)
    {
        var handler = new JwtSecurityTokenHandler();
        handler.InboundClaimTypeMap.Clear();
        var original = handler.ReadJwtToken(originalToken);

        var reissued = new JwtSecurityToken(
            issuer: original.Issuer,
            audience: original.Audiences.First(),
            claims: original.Claims,
            expires: original.ValidTo,
            signingCredentials: new SigningCredentials(
                new RsaSecurityKey(signingRsa) { KeyId = "retired-key" },
                SecurityAlgorithms.RsaSha256));

        return handler.WriteToken(reissued);
    }

    private static async Task<string> GetAdminTokenAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new
        {
            Email = AuthCenterWebApplicationFactory.AdminEmail,
            Password = AuthCenterWebApplicationFactory.AdminPassword,
            ApplicationCode = "AUTHCENTER"
        });
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<ApiResponse<AuthResponse>>();
        return body!.Data!.AccessToken;
    }
}
