using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AuthCenter.Contracts.Requests.Auth;
using AuthCenter.Contracts.Requests.Federation;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Responses.Auth;
using AuthCenter.Contracts.Responses.Federation;
using AuthCenter.Domain.Constants;
using AuthCenter.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuthCenter.IntegrationTests;

public sealed class FederationTests : IClassFixture<AuthCenterWebApplicationFactory>
{
    private readonly AuthCenterWebApplicationFactory _factory;
    public FederationTests(AuthCenterWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task OidcProvider_ProtectsSecret_AndRoutesByApplicationDomain()
    {
        using var client = await CreateAdminClientAsync();
        Guid applicationId;
        using (var scope = _factory.Services.CreateScope())
            applicationId = await scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>().ApplicationSystems.Where(item => item.Code == DomainConstants.SystemCodes.AuthCenter).Select(item => item.Id).SingleAsync();

        const string secret = "upstream-client-secret-never-returned";
        var created = await ReadDataAsync<FederationProviderDto>(await client.PostAsJsonAsync("/api/federation/providers", new UpsertFederationProviderRequest
        {
            ApplicationSystemId = applicationId,
            Name = $"Corporate-{Guid.NewGuid():N}",
            Protocol = "Oidc",
            Issuer = "https://login.example.test",
            ClientId = "authcenter-tests",
            ClientSecret = secret,
            OidcCallbackUrl = "https://authcenter.example.test/api/federation/oidc/callback",
            JitProvisioningEnabled = true,
            AccountLinkingMode = "VerifiedEmail"
        }));
        Assert.True(created.HasClientSecret);

        using (var scope = _factory.Services.CreateScope())
        {
            var stored = await scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>().FederationProviders.AsNoTracking().SingleAsync(item => item.Id == created.Id);
            Assert.NotEqual(secret, stored.ProtectedClientSecret);
            Assert.DoesNotContain(secret, stored.ProtectedClientSecret!, StringComparison.Ordinal);
        }

        (await client.PostAsJsonAsync("/api/federation/routing-rules", new CreateFederationRoutingRuleRequest
        {
            FederationProviderId = created.Id,
            Priority = 10,
            EmailDomain = "example.com"
        })).EnsureSuccessStatusCode();
        var route = await ReadDataAsync<FederationRouteResponse>(await client.PostAsJsonAsync("/api/federation/route", new FederationRouteRequest
        {
            ApplicationCode = DomainConstants.SystemCodes.AuthCenter,
            Email = "person@example.com"
        }));
        Assert.Equal(created.Id, route.ProviderId);
        Assert.Equal("Oidc", route.Protocol);
    }

    [Fact]
    public async Task ProviderConfiguration_RejectsInsecureIssuerAndMissingSamlCertificate()
    {
        using var client = await CreateAdminClientAsync();
        Guid applicationId;
        using (var scope = _factory.Services.CreateScope())
            applicationId = await scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>().ApplicationSystems.Select(item => item.Id).FirstAsync();
        var oidc = await client.PostAsJsonAsync("/api/federation/providers", new UpsertFederationProviderRequest { ApplicationSystemId = applicationId, Name = "bad-oidc", Protocol = "Oidc", Issuer = "http://insecure.test", ClientId = "x", OidcCallbackUrl = "https://callback.test" });
        Assert.Equal(HttpStatusCode.BadRequest, oidc.StatusCode);
        var saml = await client.PostAsJsonAsync("/api/federation/providers", new UpsertFederationProviderRequest { ApplicationSystemId = applicationId, Name = "bad-saml", Protocol = "Saml2", Issuer = "https://idp.test", SamlSingleSignOnUrl = "https://idp.test/sso" });
        Assert.Equal(HttpStatusCode.BadRequest, saml.StatusCode);
    }

    private async Task<HttpClient> CreateAdminClientAsync()
    {
        var client = _factory.CreateClient();
        var auth = await ReadDataAsync<AuthResponse>(await client.PostAsJsonAsync("/api/auth/login", new LoginRequest { Email = AuthCenterWebApplicationFactory.AdminEmail, Password = AuthCenterWebApplicationFactory.AdminPassword, ApplicationCode = DomainConstants.SystemCodes.AuthCenter }));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return client;
    }

    private static async Task<T> ReadDataAsync<T>(HttpResponseMessage response) where T : class
    {
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<T>>();
        Assert.NotNull(body);
        Assert.NotNull(body.Data);
        return body.Data;
    }
}
