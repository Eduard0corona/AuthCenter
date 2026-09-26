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
