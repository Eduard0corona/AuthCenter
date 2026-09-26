using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AuthCenter.Contracts.Requests.Auth;
using AuthCenter.Domain.Constants;

namespace AuthCenter.IntegrationTests;

/// <summary>
/// The administrative operations that run in a transaction, on SQL Server with the production
/// retrying execution strategy: a transaction opened outside the strategy is refused by EF Core.
/// </summary>
public sealed class AdminTransactionsRelationalTests
{
    [RelationalFact]
    public async Task TransactionalAdminOperations_RunUnderTheRetryingExecutionStrategy()
    {
        await using var factory = new SqlServerWebApplicationFactory();
        using var admin = await CreateAdminClientAsync(factory);
        var applicationId = await DataIdAsync(await admin.GetAsync("/api/applications?page=1&pageSize=1"), items: true);
        var email = $"tx-{Guid.NewGuid():N}@example.com";
        var userId = await DataIdAsync(await admin.PostAsJsonAsync("/api/users", new { fullName = "Transaction Subject", email, applicationSystemId = applicationId, grantApplicationAccess = true }));
        var groupId = await DataIdAsync(await admin.PostAsJsonAsync("/api/groups", new { name = $"Transactions {Guid.NewGuid():N}" }));

        await AssertOkAsync(await admin.PutAsJsonAsync($"/api/groups/{groupId}/access", new { applicationSystemIds = new[] { applicationId }, roleIds = Array.Empty<Guid>() }));
        await AssertOkAsync(await admin.PutAsJsonAsync($"/api/users/{userId}/access", new { applicationSystemIds = new[] { applicationId }, roleIds = Array.Empty<Guid>() }));
        var roleId = await DataIdAsync(await admin.PostAsJsonAsync("/api/roles", new { applicationSystemId = applicationId, name = $"Tx{Guid.NewGuid():N}"[..20] }));
        await AssertOkAsync(await admin.PostAsync($"/api/users/{userId}/roles/{roleId}", null));
        await AssertOkAsync(await admin.DeleteAsync($"/api/users/{userId}/roles/{roleId}"));
        await AssertOkAsync(await admin.DeleteAsync($"/api/users/{userId}/applications/{applicationId}"));
        await AssertOkAsync(await admin.PatchAsync($"/api/users/{userId}/deactivate", null));
        await admin.AddReauthenticationProofAsync(SqlServerWebApplicationFactory.AdminPassword, "admin.user.delete");
        await AssertOkAsync(await admin.DeleteAsync($"/api/users/{userId}"));
    }

    [RelationalFact]
    public async Task ScimUserWrites_AreAtomic()
    {
        const string enterprise = "urn:ietf:params:scim:schemas:extension:enterprise:2.0:User";
        const string employeeNumber = $"{enterprise}:employeeNumber";
        await using var factory = new SqlServerWebApplicationFactory();
        using var admin = await CreateAdminClientAsync(factory);
        var applicationId = await DataIdAsync(await admin.GetAsync("/api/applications?page=1&pageSize=1"), items: true);
        var definitionId = await DataIdAsync(await admin.PostAsJsonAsync("/api/profile-schema", new { key = $"level-{Guid.NewGuid():N}"[..20], displayName = "Level", dataType = "Integer" }));
        await DataIdAsync(await admin.PostAsJsonAsync("/api/lifecycle/profile-mappings", new { applicationSystemId = applicationId, sourcePath = employeeNumber, targetAttributeDefinitionId = definitionId }));
        var token = JsonDocument.Parse(await (await admin.PostAsJsonAsync("/api/provisioning-tokens", new { applicationSystemId = applicationId, name = "relational", scopes = new[] { "scim.users.read", "scim.users.write" }, expiresAt = DateTime.UtcNow.AddHours(1) })).Content.ReadAsStringAsync())
            .RootElement.GetProperty("data").GetProperty("token").GetString();
        using var scim = factory.CreateClient();
        scim.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // A refused mapped value leaves no user behind.
        var refusedEmail = $"refused-{Guid.NewGuid():N}@example.com";
        Assert.Equal(HttpStatusCode.BadRequest, (await scim.PostAsJsonAsync("/scim/v2/Users", new Dictionary<string, object> { ["userName"] = refusedEmail, [enterprise] = new { employeeNumber = "many" } })).StatusCode);
        var search = await scim.GetAsync($"/scim/v2/Users?filter={Uri.EscapeDataString($"userName eq \"{refusedEmail}\"")}");
        Assert.Equal(0, JsonDocument.Parse(await search.Content.ReadAsStringAsync()).RootElement.GetProperty("totalResults").GetInt32());

        // Nor a half-applied change: the new userName is rolled back with the refused value.
        var email = $"atomic-{Guid.NewGuid():N}@example.com";
        var created = await scim.PostAsJsonAsync("/scim/v2/Users", new Dictionary<string, object> { ["userName"] = email, [enterprise] = new { employeeNumber = 7 } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = JsonDocument.Parse(await created.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString();
        var patch = new HttpRequestMessage(HttpMethod.Patch, $"/scim/v2/Users/{id}")
        {
            Content = JsonContent.Create(new { Operations = new object[] { new { op = "replace", path = "userName", value = $"renamed-{email}" }, new { op = "replace", path = employeeNumber, value = "many" } } })
        };
        Assert.Equal(HttpStatusCode.BadRequest, (await scim.SendAsync(patch)).StatusCode);
        var user = JsonDocument.Parse(await (await scim.GetAsync($"/scim/v2/Users/{id}")).Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(email, user.GetProperty("userName").GetString());
        Assert.Equal(7, user.GetProperty(enterprise).GetProperty("employeeNumber").GetInt32());
    }

    private static async Task AssertOkAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri?.AbsolutePath}: {(int)response.StatusCode} {body}");
    }

    private static async Task<Guid> DataIdAsync(HttpResponseMessage response, bool items = false)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");
        var data = JsonDocument.Parse(body).RootElement.GetProperty("data");
        return (items ? data.GetProperty("items")[0] : data).GetProperty("id").GetGuid();
    }

    private static async Task<HttpClient> CreateAdminClientAsync(SqlServerWebApplicationFactory factory)
    {
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = SqlServerWebApplicationFactory.AdminEmail,
            Password = SqlServerWebApplicationFactory.AdminPassword,
            ApplicationCode = DomainConstants.SystemCodes.AuthCenter
        });
        var body = await login.Content.ReadAsStringAsync();
        Assert.True(login.IsSuccessStatusCode, body);
        var token = JsonDocument.Parse(body).RootElement.GetProperty("data").GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
