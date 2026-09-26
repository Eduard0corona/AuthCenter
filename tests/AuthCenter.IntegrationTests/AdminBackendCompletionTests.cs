using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AuthCenter.Application.Interfaces;
using AuthCenter.Contracts.Requests.Applications;
using AuthCenter.Contracts.Requests.Auth;
using AuthCenter.Contracts.Requests.Lifecycle;
using AuthCenter.Contracts.Requests.Permissions;
using AuthCenter.Contracts.Requests.Profiles;
using AuthCenter.Contracts.Requests.Roles;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Responses.Applications;
using AuthCenter.Contracts.Responses.Auth;
using AuthCenter.Contracts.Responses.Lifecycle;
using AuthCenter.Contracts.Responses.Permissions;
using AuthCenter.Contracts.Responses.Roles;
using AuthCenter.Domain.Constants;
using AuthCenter.Domain.Entities;
using AuthCenter.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuthCenter.IntegrationTests;

public sealed class AdminBackendCompletionTests : IClassFixture<AuthCenterWebApplicationFactory>
{
    private readonly AuthCenterWebApplicationFactory _factory;
    public AdminBackendCompletionTests(AuthCenterWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task LifecycleAdminContracts_ArePagedExplainableConcurrentAndIdempotent()
    {
        using var admin = await CreateAdminClientAsync();
        var appId = await GetApplicationIdAsync();

        var issued = await ReadDataAsync<ProvisioningTokenResponse>(await admin.PostAsJsonAsync("/api/provisioning-tokens", new CreateProvisioningTokenRequest
        {
            ApplicationSystemId = appId, Name = $"metadata-{Guid.NewGuid():N}", Scopes = ["scim.users.read"], ExpiresAt = DateTime.UtcNow.AddHours(2)
        }));
        var tokenPageResponse = await admin.GetAsync($"/api/provisioning-tokens?applicationSystemId={appId}&status=active&page=1&pageSize=10");
        var tokenPage = await ReadDataAsync<PagedResult<ProvisioningTokenMetadataDto>>(tokenPageResponse);
        Assert.Contains(tokenPage.Items, x => x.Id == issued.Id && x.Status == "active" && x.Scopes.SequenceEqual(["scim.users.read"]));
        Assert.DoesNotContain(issued.Token, await tokenPageResponse.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        Guid definitionId; Guid groupId; Guid hookId; Guid deliveryId;
        using (var scope = _factory.Services.CreateScope())
        {
            var profiles = scope.ServiceProvider.GetRequiredService<IUserProfileService>();
            var definition = await profiles.CreateDefinitionAsync(new CreateProfileAttributeDefinitionRequest { Key = $"region-{Guid.NewGuid():N}"[..24], DisplayName = "Region", DataType = "String", MaxLength = 50 });
            Assert.True(definition.IsSuccess); definitionId = definition.Data!.Id;
            var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
            groupId = Guid.NewGuid(); db.DirectoryGroups.Add(new DirectoryGroup { Id = groupId, Name = $"Preview {Guid.NewGuid():N}", NormalizedName = $"PREVIEW {Guid.NewGuid():N}", IsActive = true, CreatedAt = DateTime.UtcNow });
            var protector = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>().CreateProtector("AuthCenter.EventHookSecrets.v1");
            hookId = Guid.NewGuid(); deliveryId = Guid.NewGuid();
            db.EventHooks.Add(new EventHook { Id = hookId, ApplicationSystemId = appId, Name = $"Paged {Guid.NewGuid():N}", Url = "https://example.com/hooks", ProtectedSecret = protector.Protect("not-returned"), EventTypesJson = "[\"TEST\"]", IsVerified = true, IsActive = true, CreatedAt = DateTime.UtcNow, VerifiedAt = DateTime.UtcNow });
            db.EventHookDeliveries.Add(new EventHookDelivery { Id = deliveryId, EventHookId = hookId, EventId = Guid.NewGuid(), EventType = "TEST", PayloadJson = "{}", AttemptCount = 5, NextAttemptAt = DateTime.UtcNow, DeadLetteredAt = DateTime.UtcNow, LastError = "sanitized failure" });
            await db.SaveChangesAsync();
        }

        var mappingRequest = new CreateProfileMappingRequest { ApplicationSystemId = appId, SourcePath = "profile.region", TargetAttributeDefinitionId = definitionId, IsAuthoritative = true };
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/lifecycle/profile-mappings/validate", mappingRequest)).StatusCode);
        var mapping = await ReadDataAsync<ProfileMappingDto>(await admin.PostAsJsonAsync("/api/lifecycle/profile-mappings", mappingRequest));
        var simulated = await ReadDataAsync<ProfileMappingSimulationDto>(await admin.PostAsJsonAsync($"/api/lifecycle/profile-mappings/{mapping.Id}/simulate", new ProfileMappingSimulationRequest { SourceDocument = JsonDocument.Parse("{\"profile\":{\"region\":\"north\"}}").RootElement.Clone() }));
        Assert.True(simulated.IsValid); Assert.Equal("north", simulated.Value?.GetString());
        var updated = await ReadDataAsync<ProfileMappingDto>(await admin.PutAsJsonAsync($"/api/lifecycle/profile-mappings/{mapping.Id}", new UpdateProfileMappingRequest { SourcePath = "region", TargetAttributeDefinitionId = definitionId, IsAuthoritative = true, IsActive = true, Version = mapping.Version }));
        Assert.Equal(mapping.Version + 1, updated.Version);
        var stale = await admin.PutAsJsonAsync($"/api/lifecycle/profile-mappings/{mapping.Id}", new UpdateProfileMappingRequest { SourcePath = "stale", TargetAttributeDefinitionId = definitionId, IsActive = true, Version = mapping.Version });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode); Assert.Equal("CONCURRENCY_CONFLICT", (await stale.Content.ReadFromJsonAsync<ApiResponse<object>>())?.ErrorCode);

        using var expected = JsonDocument.Parse("\"north\"");
        var rule = await ReadDataAsync<DynamicGroupRuleDto>(await admin.PostAsJsonAsync("/api/lifecycle/group-rules", new CreateDynamicGroupRuleRequest { DirectoryGroupId = groupId, ProfileAttributeDefinitionId = definitionId, ExpectedValue = expected.RootElement.Clone() }));
        var preview = await ReadDataAsync<GroupRulePreviewDto>(await admin.PostAsJsonAsync($"/api/lifecycle/group-rules/{rule.Id}/preview", new GroupRulePreviewRequest { Page = 1, PageSize = 5 }));
        Assert.Equal(rule.Id, preview.RuleId); Assert.Equal(0, preview.Users.TotalCount);

        var deliveries = await ReadDataAsync<PagedResult<EventHookDeliveryDto>>(await admin.GetAsync($"/api/event-hooks/deliveries?hookId={hookId}&status=dead-letter&page=1&pageSize=10"));
        Assert.Single(deliveries.Items); Assert.Equal("dead-letter", deliveries.Items[0].Status);
        var hook = await ReadDataAsync<EventHookDto>(await admin.GetAsync($"/api/event-hooks/{hookId}"));
        var updatedHook = await ReadDataAsync<EventHookDto>(await admin.PutAsJsonAsync($"/api/event-hooks/{hookId}", new UpdateEventHookRequest { Name = hook.Name, Url = hook.Url, EventTypes = ["USER_CREATED", "USER_INVITED"], IsActive = true, Version = hook.Version }));
        Assert.Equal(hook.Version + 1, updatedHook.Version); Assert.Contains("USER_INVITED", updatedHook.EventTypes);
        var staleHook = await admin.PutAsJsonAsync($"/api/event-hooks/{hookId}", new UpdateEventHookRequest { Name = hook.Name, Url = hook.Url, EventTypes = ["USER_CREATED"], IsActive = true, Version = hook.Version });
        Assert.Equal(HttpStatusCode.Conflict, staleHook.StatusCode);
        var replay = new HttpRequestMessage(HttpMethod.Post, $"/api/event-hooks/deliveries/{deliveryId}/replay"); replay.Headers.Add("Idempotency-Key", "replay-contract-test");
        Assert.Equal(HttpStatusCode.OK, (await admin.SendAsync(replay)).StatusCode);
        var repeated = new HttpRequestMessage(HttpMethod.Post, $"/api/event-hooks/deliveries/{deliveryId}/replay"); repeated.Headers.Add("Idempotency-Key", "replay-contract-test");
        Assert.Equal(HttpStatusCode.OK, (await admin.SendAsync(repeated)).StatusCode);
        using (var scope = _factory.Services.CreateScope())
        {
            var stored = await scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>().EventHookDeliveries.AsNoTracking().SingleAsync(x => x.Id == deliveryId);
            Assert.Null(stored.DeadLetteredAt); Assert.Equal("replay-contract-test", stored.LastReplayIdempotencyKey);
        }
    }

    [Fact]
    public async Task OperationalContracts_ExposeDashboardTraceMetadataAndSanitizedAudit()
    {
        using var admin = await CreateAdminClientAsync();
        var appCode = $"AUD{Guid.NewGuid():N}"[..12].ToUpperInvariant();
        var app = await ReadDataAsync<ApplicationDto>(await admin.PostAsJsonAsync("/api/applications", new CreateApplicationRequest { Code = appCode, Name = "Audit contract app", RegistrationMode = "Closed", AllowPasswordLogin = true }));
        var permission = await ReadDataAsync<PermissionDto>(await admin.PostAsJsonAsync("/api/permissions", new CreatePermissionRequest { ApplicationSystemId = app.Id, Code = $"{appCode}_READ", Name = "Read" }));
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/permissions", new CreatePermissionRequest { ApplicationSystemId = app.Id, Code = permission.Code, Name = "Duplicate" })).StatusCode);
        var role = await ReadDataAsync<RoleDto>(await admin.PostAsJsonAsync("/api/roles", new CreateRoleRequest { ApplicationSystemId = app.Id, Name = "Audited role" }));
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/roles/{role.Id}/permissions/{permission.Id}", null)).StatusCode);

        var dashboard = await ReadDataAsync<AdminDashboardDto>(await admin.GetAsync("/api/admin-dashboard"));
        Assert.True(dashboard.ActiveUsers > 0); Assert.True(dashboard.ActiveApplications > 0); Assert.NotEqual(default, dashboard.GeneratedAt);
        var version = await ReadDataAsync<VersionManifestDto>(await admin.GetAsync("/api/version")); Assert.False(string.IsNullOrWhiteSpace(version.Version)); Assert.Equal("/admin-v2", version.AdminFrontendBasePath);
        var metadata = await ReadDataAsync<AdminApiMetadataDto>(await admin.GetAsync("/api/admin-metadata"));
        Assert.Equal(100, metadata.MaximumPageSize); Assert.Contains("CONCURRENCY_CONFLICT", metadata.ErrorCodes.Keys); Assert.Equal(DomainConstants.Permissions.ProvisioningWrite, metadata.OperationPermissions["POST /api/provisioning-tokens"]);

        var missing = await admin.GetAsync($"/api/applications/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        var missingBody = await missing.Content.ReadFromJsonAsync<ApiResponse<object>>();
        Assert.False(string.IsNullOrWhiteSpace(missingBody?.TraceId));

        var export = await admin.GetAsync($"/api/audit-logs/export?applicationCode={appCode}&page=1&pageSize=100");
        export.EnsureSuccessStatusCode(); Assert.Equal("text/csv", export.Content.Headers.ContentType?.MediaType);
        var csv = await export.Content.ReadAsStringAsync(); Assert.Contains("APPLICATION_CREATED", csv); Assert.DoesNotContain(AuthCenterWebApplicationFactory.AdminPassword, csv, StringComparison.Ordinal);
        using var scope = _factory.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
        var actions = await db.AuditLogs.AsNoTracking().Where(x => x.ApplicationCode == appCode).ToListAsync();
        Assert.Contains(actions, x => x.Action == "APPLICATION_CREATED" && x.UserId.HasValue && x.EntityId == app.Id.ToString() && x.TraceId != null && x.MetadataJson!.Contains("Success"));
        Assert.Contains(actions, x => x.Action == "PERMISSION_CREATED"); Assert.Contains(actions, x => x.Action == "ROLE_CREATED"); Assert.Contains(actions, x => x.Action == "ROLE_PERMISSION_GRANTED");
        Assert.True(await db.AuditLogs.AnyAsync(x => x.Action == "SYSTEM_LOG_EXPORTED"));
        Assert.True(await db.AuditLogs.AnyAsync(x => x.Action == "ADMIN_MUTATION_REJECTED" && x.MetadataJson!.Contains("Rejected") && !x.MetadataJson.Contains("Password", StringComparison.OrdinalIgnoreCase)));
    }

    private async Task<Guid> GetApplicationIdAsync() { using var scope = _factory.Services.CreateScope(); return await scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>().ApplicationSystems.Where(x => x.Code == DomainConstants.SystemCodes.AuthCenter).Select(x => x.Id).SingleAsync(); }
    private async Task<HttpClient> CreateAdminClientAsync() { var client = _factory.CreateClient(); var auth = await ReadDataAsync<AuthResponse>(await client.PostAsJsonAsync("/api/auth/login", new LoginRequest { Email = AuthCenterWebApplicationFactory.AdminEmail, Password = AuthCenterWebApplicationFactory.AdminPassword, ApplicationCode = DomainConstants.SystemCodes.AuthCenter })); client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken); return client; }
    private static async Task<T> ReadDataAsync<T>(HttpResponseMessage response) where T : class { response.EnsureSuccessStatusCode(); var body = await response.Content.ReadFromJsonAsync<ApiResponse<T>>(); Assert.NotNull(body?.Data); return body.Data; }
}
