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
        await client.AddReauthenticationProofAsync(AuthCenterWebApplicationFactory.AdminPassword, "admin.federation.change");
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

        await client.AddReauthenticationProofAsync(AuthCenterWebApplicationFactory.AdminPassword, "admin.federation.change");
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
        await client.AddReauthenticationProofAsync(AuthCenterWebApplicationFactory.AdminPassword, "admin.federation.change");
        var oidc = await client.PostAsJsonAsync("/api/federation/providers", new UpsertFederationProviderRequest { ApplicationSystemId = applicationId, Name = "bad-oidc", Protocol = "Oidc", Issuer = "http://insecure.test", ClientId = "x", OidcCallbackUrl = "https://callback.test" });
        Assert.Equal(HttpStatusCode.BadRequest, oidc.StatusCode);
        await client.AddReauthenticationProofAsync(AuthCenterWebApplicationFactory.AdminPassword, "admin.federation.change");
        var saml = await client.PostAsJsonAsync("/api/federation/providers", new UpsertFederationProviderRequest { ApplicationSystemId = applicationId, Name = "bad-saml", Protocol = "Saml2", Issuer = "https://idp.test", SamlSingleSignOnUrl = "https://idp.test/sso" });
        Assert.Equal(HttpStatusCode.BadRequest, saml.StatusCode);
    }

    [Fact]
    public async Task SamlProvider_KeepsStoredCertificateWhenUpdateOmitsPem()
    {
        using var client = await CreateAdminClientAsync();
        Guid applicationId;
        using (var scope = _factory.Services.CreateScope())
            applicationId = await scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>().ApplicationSystems.Where(item => item.Code == DomainConstants.SystemCodes.AuthCenter).Select(item => item.Id).SingleAsync();
        var pem = CreateCertificatePem();

        await client.AddReauthenticationProofAsync(AuthCenterWebApplicationFactory.AdminPassword, "admin.federation.change");
        var created = await ReadDataAsync<FederationProviderDto>(await client.PostAsJsonAsync("/api/federation/providers", new UpsertFederationProviderRequest
        {
            ApplicationSystemId = applicationId, Name = $"Saml-{Guid.NewGuid():N}", Protocol = "Saml2", Issuer = "https://idp.example.test",
            SamlSingleSignOnUrl = "https://idp.example.test/sso", SamlSigningCertificatePem = pem, AccountLinkingMode = "Disabled"
        }));
        Assert.False(string.IsNullOrEmpty(created.SamlSigningCertificateThumbprint));

        await client.AddReauthenticationProofAsync(AuthCenterWebApplicationFactory.AdminPassword, "admin.federation.change");
        var updated = await ReadDataAsync<FederationProviderDto>(await client.PutAsJsonAsync($"/api/federation/providers/{created.Id}", new UpsertFederationProviderRequest
        {
            ApplicationSystemId = applicationId, Name = created.Name, Protocol = "Saml2", Issuer = created.Issuer,
            SamlSingleSignOnUrl = "https://idp.example.test/sso2", SamlSigningCertificatePem = null, AccountLinkingMode = "Disabled", IsActive = false, Version = created.Version
        }));
        Assert.Equal(created.SamlSigningCertificateThumbprint, updated.SamlSigningCertificateThumbprint);
        Assert.Equal("https://idp.example.test/sso2", updated.SamlSingleSignOnUrl);
        Assert.False(updated.IsActive);

        await client.AddReauthenticationProofAsync(AuthCenterWebApplicationFactory.AdminPassword, "admin.federation.change");
        var invalid = await client.PutAsJsonAsync($"/api/federation/providers/{created.Id}", new UpsertFederationProviderRequest
        {
            ApplicationSystemId = applicationId, Name = created.Name, Protocol = "Saml2", Issuer = created.Issuer,
            SamlSingleSignOnUrl = "https://idp.example.test/sso2", SamlSigningCertificatePem = "not-a-certificate", AccountLinkingMode = "Disabled", Version = updated.Version
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    private static string CreateCertificatePem()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=AuthCenter Federation Tests", rsa, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        return certificate.ExportCertificatePem();
    }

    [Fact]
    public async Task RoutingRules_AreListableVersionedReorderableAndProtectedByStepUp()
    {
        using var client = await CreateAdminClientAsync();
        Guid applicationId;
        using (var scope = _factory.Services.CreateScope()) applicationId = await scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>().ApplicationSystems.Where(x => x.Code == DomainConstants.SystemCodes.AuthCenter).Select(x => x.Id).SingleAsync();
        var withoutProof = await client.PostAsJsonAsync("/api/federation/providers", new UpsertFederationProviderRequest { ApplicationSystemId = applicationId, Name = "step-up-rejected", Protocol = "Oidc", Issuer = "https://login.example.test", ClientId = "test", OidcCallbackUrl = "https://authcenter.example.test/callback", AccountLinkingMode = "VerifiedEmail" });
        Assert.Equal(HttpStatusCode.Forbidden, withoutProof.StatusCode);
        await client.AddReauthenticationProofAsync(AuthCenterWebApplicationFactory.AdminPassword, "admin.federation.change");
        var provider = await ReadDataAsync<FederationProviderDto>(await client.PostAsJsonAsync("/api/federation/providers", new UpsertFederationProviderRequest { ApplicationSystemId = applicationId, Name = $"Routing-{Guid.NewGuid():N}", Protocol = "Oidc", Issuer = "https://login.example.test", ClientId = "test", OidcCallbackUrl = "https://authcenter.example.test/callback", AccountLinkingMode = "VerifiedEmail" }));
        foreach (var item in new[] { (Priority: 10, Domain: "first.example"), (Priority: 20, Domain: "second.example") })
        {
            await client.AddReauthenticationProofAsync(AuthCenterWebApplicationFactory.AdminPassword, "admin.federation.change");
            (await client.PostAsJsonAsync("/api/federation/routing-rules", new CreateFederationRoutingRuleRequest { FederationProviderId = provider.Id, Priority = item.Priority, EmailDomain = item.Domain })).EnsureSuccessStatusCode();
        }
        var rules = await ReadDataAsync<List<FederationRoutingRuleDto>>(await client.GetAsync($"/api/federation/routing-rules?applicationSystemId={applicationId}"));
        var owned = rules.Where(x => x.FederationProviderId == provider.Id).OrderBy(x => x.Priority).ToList(); Assert.Equal(2, owned.Count);
        await client.AddReauthenticationProofAsync(AuthCenterWebApplicationFactory.AdminPassword, "admin.federation.change");
        var updated = await ReadDataAsync<FederationRoutingRuleDto>(await client.PutAsJsonAsync($"/api/federation/routing-rules/{owned[0].Id}", new UpdateFederationRoutingRuleRequest { Priority = 11, EmailDomain = owned[0].EmailDomain, IsActive = true, Version = owned[0].Version }));
        Assert.Equal(11, updated.Priority);
        await client.AddReauthenticationProofAsync(AuthCenterWebApplicationFactory.AdminPassword, "admin.federation.change");
        var stale = await client.PutAsJsonAsync($"/api/federation/routing-rules/{owned[0].Id}", new UpdateFederationRoutingRuleRequest { Priority = 12, EmailDomain = owned[0].EmailDomain, IsActive = true, Version = owned[0].Version });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        await client.AddReauthenticationProofAsync(AuthCenterWebApplicationFactory.AdminPassword, "admin.federation.change");
        var reorder = await client.PutAsJsonAsync("/api/federation/routing-rules/order", new ReorderFederationRoutingRulesRequest { Rules = [new FederationRoutingRuleOrderItem { Id = updated.Id, Priority = 30, Version = updated.Version }, new FederationRoutingRuleOrderItem { Id = owned[1].Id, Priority = 10, Version = owned[1].Version }] });
        Assert.Equal(HttpStatusCode.OK, reorder.StatusCode);
        using var scope2 = _factory.Services.CreateScope(); var db = scope2.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
        Assert.True(await db.AuditLogs.AnyAsync(x => x.Action == "FEDERATION_CHANGE_REJECTED" && x.MetadataJson!.Contains("ReauthenticationRequired")));
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
