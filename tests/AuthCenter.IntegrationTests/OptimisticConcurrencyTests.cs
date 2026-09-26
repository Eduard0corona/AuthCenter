using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AuthCenter.Contracts.Requests.Auth;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Responses.Auth;
using AuthCenter.Domain.Constants;

namespace AuthCenter.IntegrationTests;

/// <summary>
/// ADM-02: an update that names the version it loaded is refused with 409 when the record changed
/// since then; without a version the last write wins, as before.
/// </summary>
public sealed class OptimisticConcurrencyTests : IClassFixture<AuthCenterWebApplicationFactory>
{
    private readonly AuthCenterWebApplicationFactory _factory;

    public OptimisticConcurrencyTests(AuthCenterWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Applications_Roles_Permissions_AndGroups_RejectStaleUpdates()
    {
        using var admin = await CreateAdminClientAsync();
        var code = $"OCC{Guid.NewGuid():N}"[..12].ToUpperInvariant();
        var application = await DataAsync(await admin.PostAsJsonAsync("/api/applications", new { code, name = "Concurrency app", registrationMode = "Closed", allowPasswordLogin = true }));
        var applicationId = application.GetProperty("id").GetString();
        await AssertVersionedAsync(application, version => admin.PutAsJsonAsync($"/api/applications/{applicationId}", new { name = "Renamed app", registrationMode = "Closed", allowPasswordLogin = true, version }));

        var role = await DataAsync(await admin.PostAsJsonAsync("/api/roles", new { applicationSystemId = applicationId, name = "Operators" }));
        await AssertVersionedAsync(role, version => admin.PutAsJsonAsync($"/api/roles/{role.GetProperty("id").GetString()}", new { name = $"Operators {version}", version }));

        var permission = await DataAsync(await admin.PostAsJsonAsync("/api/permissions", new { applicationSystemId = applicationId, code = $"{code}_READ", name = "Read" }));
        await AssertVersionedAsync(permission, version => admin.PutAsJsonAsync($"/api/permissions/{permission.GetProperty("id").GetString()}", new { name = "Read all", version }));

        var group = await DataAsync(await admin.PostAsJsonAsync("/api/groups", new { name = $"Concurrency {Guid.NewGuid():N}" }));
        await AssertVersionedAsync(group, version => admin.PutAsJsonAsync($"/api/groups/{group.GetProperty("id").GetString()}", new { name = $"Concurrency {Guid.NewGuid():N}", version }));
    }

    [Fact]
    public async Task OAuthClients_PolicyRules_ProfileAttributes_ApiResources_AndUsers_RejectStaleUpdates()
    {
        using var admin = await CreateAdminClientAsync();
        var code = $"OCC{Guid.NewGuid():N}"[..12].ToUpperInvariant();
        var application = await DataAsync(await admin.PostAsJsonAsync("/api/applications", new { code, name = "Concurrency app", registrationMode = "Open", allowPasswordLogin = true }));
        var applicationId = application.GetProperty("id").GetString();

        var clientId = $"occ-{Guid.NewGuid():N}"[..20];
        var created = await DataAsync(await admin.PostAsJsonAsync("/api/oauth/clients", new
        {
            applicationSystemId = applicationId, clientId, displayName = "Concurrency client", clientType = 1,
            redirectUris = new[] { "https://app.example.test/callback" }, allowedScopes = new[] { "openid" }, grantTypes = new[] { "authorization_code" },
            loginUrl = "https://app.example.test/login", requirePkce = true
        }));
        await AssertVersionedAsync(created.GetProperty("client"), version => admin.PutAsJsonAsync($"/api/oauth/clients/{clientId}", new
        {
            displayName = "Renamed client", redirectUris = new[] { "https://app.example.test/callback" }, allowedScopes = new[] { "openid" },
            grantTypes = new[] { "authorization_code" }, loginUrl = "https://app.example.test/login", requirePkce = true, isActive = true, version
        }));

        var draft = await DataAsync(await admin.PostAsync($"/api/access-policies/applications/{applicationId}/drafts", null));
        var rule = await DataAsync(await admin.PostAsJsonAsync("/api/access-policies", new { applicationSystemId = applicationId, policyVersionId = draft.GetProperty("id").GetString(), name = "Allow all", priority = 100, action = "Allow" }));
        await AssertVersionedAsync(rule, version => admin.PutAsJsonAsync($"/api/access-policies/{rule.GetProperty("id").GetString()}", new { name = "Allow everyone", priority = 100, action = "Allow", version }));

        var attribute = await DataAsync(await admin.PostAsJsonAsync("/api/profile-schema", new { key = $"occ_{Guid.NewGuid():N}"[..20], displayName = "Cost center", dataType = "String" }));
        await AssertVersionedAsync(attribute, version => admin.PutAsJsonAsync($"/api/profile-schema/{attribute.GetProperty("id").GetString()}", new { displayName = "Cost centre", dataType = "String", isActive = true, version }));

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var resource = await DataAsync(await admin.PostAsJsonAsync("/api/api-resources", new
        {
            applicationSystemId = applicationId, identifier = $"https://api.example.test/{suffix}", displayName = "Orders",
            scopes = new[] { new { name = $"orders.{suffix}", displayName = "Orders" } }
        }));
        await AssertVersionedAsync(resource, version => admin.PutAsJsonAsync($"/api/api-resources/{resource.GetProperty("id").GetString()}", new
        {
            displayName = "Orders API", isActive = true, scopes = new[] { new { name = $"orders.{suffix}", displayName = "Orders" } }, version
        }));

        var user = await DataAsync(await admin.PostAsJsonAsync("/api/users", new { fullName = "Concurrency User", email = $"occ-{Guid.NewGuid():N}@example.test" }));
        await AssertVersionedAsync(user, version => admin.PutAsJsonAsync($"/api/users/{user.GetProperty("id").GetString()}", new { fullName = $"Concurrency User {version}", version }));
    }

    /// <summary>Loaded version v: updating v succeeds (v+1), updating v again is 409, no version succeeds.</summary>
    private static async Task AssertVersionedAsync(JsonElement loaded, Func<long?, Task<HttpResponseMessage>> update)
    {
        var version = loaded.GetProperty("version").GetInt64();

        var first = await DataAsync(await update(version));
        Assert.Equal(version + 1, first.GetProperty("version").GetInt64());

        var stale = await update(version);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("CONCURRENCY_CONFLICT", (await stale.Content.ReadFromJsonAsync<ApiResponse<JsonElement>>())?.ErrorCode);

        var unversioned = await DataAsync(await update(null));
        Assert.Equal(version + 2, unversioned.GetProperty("version").GetInt64());
    }

    private static async Task<JsonElement> DataAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("data").Clone();
    }

    private async Task<HttpClient> CreateAdminClientAsync()
    {
        var client = _factory.CreateClient();
        var auth = (await (await client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = AuthCenterWebApplicationFactory.AdminEmail,
            Password = AuthCenterWebApplicationFactory.AdminPassword,
            ApplicationCode = DomainConstants.SystemCodes.AuthCenter
        })).Content.ReadFromJsonAsync<ApiResponse<AuthResponse>>())!.Data!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return client;
    }
}
