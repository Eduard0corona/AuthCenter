using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AuthCenter.Application.Interfaces;
using AuthCenter.Contracts.Requests.Auth;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Responses.Audit;
using AuthCenter.Contracts.Responses.Auth;
using AuthCenter.Domain.Constants;
using AuthCenter.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuthCenter.IntegrationTests;

/// <summary>The System Log API: entity filters, the actor of each event and a safe CSV export.</summary>
public sealed class SystemLogTests : IClassFixture<AuthCenterWebApplicationFactory>
{
    private readonly AuthCenterWebApplicationFactory _factory;

    public SystemLogTests(AuthCenterWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Events_AreFilteredByEntity_AndNameTheirActor()
    {
        var entityId = Guid.NewGuid().ToString();
        var adminId = await AdminIdAsync();
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var audit = scope.ServiceProvider.GetRequiredService<IAuditService>();
            await audit.LogAsync("EVENT_HOOK_UPDATED", adminId, entityName: "EventHook", entityId: entityId);
            await audit.LogAsync("EVENT_HOOK_VERIFIED", adminId, entityName: "EventHook", entityId: entityId);
            await audit.LogAsync("EVENT_HOOK_UPDATED", adminId, entityName: "EventHook", entityId: Guid.NewGuid().ToString());
        }
        using var admin = await CreateAdminClientAsync();

        var page = await ReadDataAsync<PagedResult<AuditLogDto>>(await admin.GetAsync($"/api/audit-logs?entityName=EventHook&entityId={entityId}&pageSize=20"));

        Assert.Equal(2, page.TotalCount);
        Assert.All(page.Items, item =>
        {
            Assert.Equal(entityId, item.EntityId);
            Assert.Equal(AuthCenterWebApplicationFactory.AdminEmail, item.UserEmail);
        });
        Assert.Equal(["EVENT_HOOK_VERIFIED", "EVENT_HOOK_UPDATED"], page.Items.Select(item => item.Action).ToArray());
    }

    [Fact]
    public async Task Export_NeutralizesSpreadsheetFormulas()
    {
        var entityId = "=HYPERLINK(\"https://evil.example\")";
        await using (var scope = _factory.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IAuditService>().LogAsync("EVENT_HOOK_UPDATED", entityName: "EventHook", entityId: entityId);
        using var admin = await CreateAdminClientAsync();

        var csv = await (await admin.GetAsync($"/api/audit-logs/export?entityName=EventHook&entityId={Uri.EscapeDataString(entityId)}")).Content.ReadAsStringAsync();

        Assert.Contains("actorEmail", csv.Split("\r\n")[0]);
        Assert.Contains("\"'=HYPERLINK(\"\"https://evil.example\"\")\"", csv);
    }

    private async Task<Guid> AdminIdAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>().Users
            .Where(user => user.Email == AuthCenterWebApplicationFactory.AdminEmail).Select(user => user.Id).SingleAsync();
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

    private static async Task<T> ReadDataAsync<T>(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        return JsonSerializer.Deserialize<ApiResponse<T>>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Data!;
    }
}
