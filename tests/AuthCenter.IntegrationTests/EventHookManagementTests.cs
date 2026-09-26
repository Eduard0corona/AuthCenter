using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AuthCenter.Contracts.Requests.Auth;
using AuthCenter.Contracts.Requests.Lifecycle;
using AuthCenter.Contracts.Requests.Users;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Responses.Auth;
using AuthCenter.Contracts.Responses.Lifecycle;
using AuthCenter.Domain.Constants;
using AuthCenter.Domain.Entities;
using AuthCenter.Infrastructure.Persistence;
using AuthCenter.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuthCenter.IntegrationTests;

/// <summary>Event hooks: the event type catalog, delivery of every audited event, secret rotation.</summary>
public sealed class EventHookManagementTests : IClassFixture<AuthCenterWebApplicationFactory>
{
    private readonly AuthCenterWebApplicationFactory _factory;

    public EventHookManagementTests(AuthCenterWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task EventTypes_AreListedByArea_AndUnknownTypesAreRejected()
    {
        using var admin = await CreateAdminClientAsync();

        var types = await ReadDataAsync<List<EventTypeDto>>(await admin.GetAsync("/api/event-hooks/event-types"));
        Assert.Contains(types, item => item.Type == "USER_INVITED" && item.Category == "users");
        Assert.Contains(types, item => item.Type == "FEDERATION_LOGIN_SUCCESS" && item.Category == "federation");
        Assert.Equal(types.Count, types.Select(item => item.Type).Distinct().Count());

        var misspelled = await admin.PostAsJsonAsync("/api/event-hooks", new CreateEventHookRequest { Name = $"typo-{Guid.NewGuid():N}", Url = "https://example.com/hooks", EventTypes = ["USER_CREATD"] });
        Assert.Equal(HttpStatusCode.BadRequest, misspelled.StatusCode);
        Assert.Equal("UNKNOWN_EVENT_TYPE", (await misspelled.Content.ReadFromJsonAsync<ApiResponse<object>>())!.ErrorCode);
    }

    [Fact]
    public async Task AuditEventsWrittenByAnyService_ReachTheSubscribedHooks()
    {
        var hookId = await CreateVerifiedHookAsync(["USER_INVITED", "APPLICATION_CREATED"]);
        using var admin = await CreateAdminClientAsync();
        var code = $"HK{Guid.NewGuid():N}"[..10].ToUpperInvariant();
        // The application service audits through IAuditService; invitations write the audit row directly.
        var application = await ReadDataAsync<JsonElement>(await admin.PostAsJsonAsync("/api/applications", new { Code = code, Name = "Hook app " + code, RegistrationMode = "Closed" }));
        var invited = await admin.PostAsJsonAsync("/api/users/invitations", new InviteUserRequest
        {
            FullName = "Invited Person",
            Email = $"invited-{Guid.NewGuid():N}@example.com",
            ApplicationSystemId = application.GetProperty("id").GetGuid()
        });
        Assert.True(invited.IsSuccessStatusCode, await invited.Content.ReadAsStringAsync());

        var deliveries = await ReadDataAsync<PagedResult<EventHookDeliveryDto>>(await admin.GetAsync($"/api/event-hooks/deliveries?hookId={hookId}&pageSize=50"));
        Assert.Contains(deliveries.Items, item => item.EventType == "APPLICATION_CREATED");
        var invitation = Assert.Single(deliveries.Items, item => item.EventType == "USER_INVITED");
        Assert.True(invitation.CreatedAt > DateTime.UtcNow.AddMinutes(-5));
        Assert.Null(invitation.Payload);

        var detail = await ReadDataAsync<EventHookDeliveryDto>(await admin.GetAsync($"/api/event-hooks/deliveries/{invitation.Id}"));
        using var payload = JsonDocument.Parse(detail.Payload!);
        Assert.Equal("USER_INVITED", payload.RootElement.GetProperty("type").GetString());
        Assert.Equal(invitation.EventId, payload.RootElement.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task RotatingTheSecret_NeedsAProof_AndSignsWithBothSecretsForTheGracePeriod()
    {
        var hookId = await CreateVerifiedHookAsync(["USER_CREATED"]);
        using var admin = await CreateAdminClientAsync();

        var withoutProof = await admin.PostAsync($"/api/event-hooks/{hookId}/rotate-secret", null);
        Assert.Equal(HttpStatusCode.Forbidden, withoutProof.StatusCode);

        await admin.AddReauthenticationProofAsync(AuthCenterWebApplicationFactory.AdminPassword, "admin.event-hook.rotate-secret");
        var rotated = await ReadDataAsync<EventHookSecretResponse>(await admin.PostAsync($"/api/event-hooks/{hookId}/rotate-secret", null));
        Assert.False(string.IsNullOrWhiteSpace(rotated.Secret));
        Assert.InRange(rotated.PreviousSecretExpiresAt!.Value, DateTime.UtcNow.AddHours(23), DateTime.UtcNow.AddHours(25));
        var listed = await ReadDataAsync<EventHookDto>(await admin.GetAsync($"/api/event-hooks/{hookId}"));
        Assert.NotNull(listed.PreviousSecretExpiresAt);

        await using var scope = _factory.Services.CreateAsyncScope();
        var protector = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>().CreateProtector("AuthCenter.EventHookSecrets.v1");
        var hook = await scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>().EventHooks.AsNoTracking().SingleAsync(item => item.Id == hookId);
        var header = EventHookDispatcherService.SignatureHeader(hook, protector, "1700000000", "{}", DateTime.UtcNow);
        Assert.Equal(
            [$"v1={EventHookDispatcherService.Sign(rotated.Secret, "1700000000", "{}")}", $"v1={EventHookDispatcherService.Sign(InitialSecret, "1700000000", "{}")}"],
            header.Split(','));
        // Once the grace period ends only the new secret signs.
        Assert.Equal($"v1={EventHookDispatcherService.Sign(rotated.Secret, "1700000000", "{}")}", EventHookDispatcherService.SignatureHeader(hook, protector, "1700000000", "{}", DateTime.UtcNow.AddHours(25)));
    }

    private const string InitialSecret = "initial-hook-secret";

    private async Task<Guid> CreateVerifiedHookAsync(string[] eventTypes)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>().CreateProtector("AuthCenter.EventHookSecrets.v1");
        var hook = new EventHook
        {
            Id = Guid.NewGuid(),
            Name = $"hook-{Guid.NewGuid():N}",
            Url = "https://example.com/hooks",
            ProtectedSecret = protector.Protect(InitialSecret),
            EventTypesJson = JsonSerializer.Serialize(eventTypes),
            IsVerified = true,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            VerifiedAt = DateTime.UtcNow
        };
        db.EventHooks.Add(hook);
        await db.SaveChangesAsync();
        return hook.Id;
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
