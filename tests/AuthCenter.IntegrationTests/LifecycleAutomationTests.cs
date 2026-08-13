using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AuthCenter.Application.Interfaces;
using AuthCenter.Contracts.Requests.Auth;
using AuthCenter.Contracts.Requests.Lifecycle;
using AuthCenter.Contracts.Requests.Profiles;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Responses.Auth;
using AuthCenter.Contracts.Responses.Lifecycle;
using AuthCenter.Domain.Constants;
using AuthCenter.Domain.Entities;
using AuthCenter.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuthCenter.IntegrationTests;

[Trait("Category", "Conformance")]
public sealed class LifecycleAutomationTests : IClassFixture<AuthCenterWebApplicationFactory>
{
    private readonly AuthCenterWebApplicationFactory _factory;
    public LifecycleAutomationTests(AuthCenterWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task ScimToken_EnforcesScopes_ProvisionsFiltersPatchesAndDeprovisionsUser()
    {
        using var admin = await CreateAdminClientAsync(); var appId = await GetApplicationIdAsync();
        var issued = await ReadDataAsync<ProvisioningTokenResponse>(await admin.PostAsJsonAsync("/api/provisioning-tokens", new CreateProvisioningTokenRequest { ApplicationSystemId = appId, Name = "scim-test", Scopes = ["scim.users.read", "scim.users.write"], ExpiresAt = DateTime.UtcNow.AddHours(1) }));
        using (var scope = _factory.Services.CreateScope()) { var stored = await scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>().ProvisioningTokens.AsNoTracking().SingleAsync(x => x.Id == issued.Id); Assert.NotEqual(issued.Token, stored.TokenHash); Assert.DoesNotContain(issued.Token, stored.TokenHash, StringComparison.Ordinal); }

        using var scim = _factory.CreateClient(); scim.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", issued.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/scim/v2/Users")).StatusCode);
        var email = $"scim-{Guid.NewGuid():N}@example.com";
        var created = await scim.PostAsJsonAsync("/scim/v2/Users", new { schemas = new[] { "urn:ietf:params:scim:schemas:core:2.0:User" }, externalId = "external-123", userName = email, displayName = "SCIM User", active = true });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode); using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync()); var id = createdJson.RootElement.GetProperty("id").GetString(); Assert.NotNull(id);

        var list = await scim.GetAsync($"/scim/v2/Users?filter={Uri.EscapeDataString($"userName eq \"{email}\"")}"); list.EnsureSuccessStatusCode(); using var listJson = JsonDocument.Parse(await list.Content.ReadAsStringAsync()); Assert.Equal(1, listJson.RootElement.GetProperty("totalResults").GetInt32());
        var patch = new HttpRequestMessage(HttpMethod.Patch, $"/scim/v2/Users/{id}") { Content = JsonContent.Create(new { schemas = new[] { "urn:ietf:params:scim:api:messages:2.0:PatchOp" }, Operations = new[] { new { op = "replace", path = "active", value = false } } }) }; var patched = await scim.SendAsync(patch); patched.EnsureSuccessStatusCode(); using var patchJson = JsonDocument.Parse(await patched.Content.ReadAsStringAsync()); Assert.False(patchJson.RootElement.GetProperty("active").GetBoolean());
        Assert.Equal(HttpStatusCode.NoContent, (await scim.DeleteAsync($"/scim/v2/Users/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await scim.GetAsync("/scim/v2/Groups")).StatusCode);
    }

    [Fact]
    public async Task ProvisioningToken_RotationRevokesOldToken_AndEventAuditEnqueuesOneDelivery()
    {
        using var admin = await CreateAdminClientAsync(); var appId = await GetApplicationIdAsync();
        var issued = await ReadDataAsync<ProvisioningTokenResponse>(await admin.PostAsJsonAsync("/api/provisioning-tokens", new CreateProvisioningTokenRequest { ApplicationSystemId = appId, Name = "rotate-test", Scopes = ["scim.groups.read", "scim.groups.write"], ExpiresAt = DateTime.UtcNow.AddHours(1) }));
        await admin.AddReauthenticationProofAsync(AuthCenterWebApplicationFactory.AdminPassword, "admin.provisioning-token.rotate");
        var rotated = await ReadDataAsync<ProvisioningTokenResponse>(await admin.PostAsync($"/api/provisioning-tokens/{issued.Id}/rotate?expiresAt={Uri.EscapeDataString(DateTime.UtcNow.AddHours(2).ToString("O"))}", null));
        using var oldClient = _factory.CreateClient(); oldClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", issued.Token); Assert.Equal(HttpStatusCode.Unauthorized, (await oldClient.GetAsync("/scim/v2/Groups")).StatusCode);
        using var newClient = _factory.CreateClient(); newClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", rotated.Token); Assert.Equal(HttpStatusCode.OK, (await newClient.GetAsync("/scim/v2/Groups")).StatusCode);
        var groupName = $"SCIM Group {Guid.NewGuid():N}"; var groupResponse = await newClient.PostAsJsonAsync("/scim/v2/Groups", new { schemas = new[] { "urn:ietf:params:scim:schemas:core:2.0:Group" }, externalId = "group-external-1", displayName = groupName, members = Array.Empty<object>() }); Assert.Equal(HttpStatusCode.Created, groupResponse.StatusCode); using var groupJson = JsonDocument.Parse(await groupResponse.Content.ReadAsStringAsync()); var groupId = groupJson.RootElement.GetProperty("id").GetString(); Assert.NotNull(groupId);
        var groupList = await newClient.GetAsync($"/scim/v2/Groups?filter={Uri.EscapeDataString($"displayName eq \"{groupName}\"")}"); groupList.EnsureSuccessStatusCode(); using var groupListJson = JsonDocument.Parse(await groupList.Content.ReadAsStringAsync()); Assert.Equal(1, groupListJson.RootElement.GetProperty("totalResults").GetInt32());
        var groupPatch = new HttpRequestMessage(HttpMethod.Patch, $"/scim/v2/Groups/{groupId}") { Content = JsonContent.Create(new { schemas = new[] { "urn:ietf:params:scim:api:messages:2.0:PatchOp" }, Operations = new[] { new { op = "replace", path = "displayName", value = $"{groupName} Updated" } } }) }; Assert.Equal(HttpStatusCode.OK, (await newClient.SendAsync(groupPatch)).StatusCode);

        Guid hookId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>(); var protection = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>().CreateProtector("AuthCenter.EventHookSecrets.v1"); hookId = Guid.NewGuid(); db.EventHooks.Add(new EventHook { Id = hookId, Name = "audit-test", Url = "https://hooks.example.com/events", ProtectedSecret = protection.Protect("test-secret"), EventTypesJson = "[\"SCIM_TEST_EVENT\"]", IsVerified = true, IsActive = true, CreatedAt = DateTime.UtcNow, VerifiedAt = DateTime.UtcNow }); await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<IAuditService>().LogAsync("SCIM_TEST_EVENT", applicationCode: DomainConstants.SystemCodes.AuthCenter);
        }
        using (var scope = _factory.Services.CreateScope()) { var delivery = await scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>().EventHookDeliveries.AsNoTracking().SingleAsync(x => x.EventHookId == hookId); Assert.Equal("SCIM_TEST_EVENT", delivery.EventType); Assert.Null(delivery.DeliveredAt); Assert.NotEqual(Guid.Empty, delivery.EventId); }
    }

    [Fact]
    public async Task ScimAuthoritativeMapping_DrivesDynamicGroupMembership()
    {
        var appId = await GetApplicationIdAsync(); Guid definitionId; Guid groupId;
        using (var scope = _factory.Services.CreateScope())
        {
            var profiles = scope.ServiceProvider.GetRequiredService<IUserProfileService>();
            var definition = await profiles.CreateDefinitionAsync(new CreateProfileAttributeDefinitionRequest { Key = $"department-{Guid.NewGuid():N}"[..28], DisplayName = "Department", DataType = "String", MaxLength = 100 }); Assert.True(definition.IsSuccess); definitionId = definition.Data!.Id;
            var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>(); groupId = Guid.NewGuid(); db.DirectoryGroups.Add(new DirectoryGroup { Id = groupId, Name = $"Engineering {Guid.NewGuid():N}", NormalizedName = $"ENGINEERING {Guid.NewGuid():N}", IsActive = true, CreatedAt = DateTime.UtcNow }); db.GroupApplicationAssignments.Add(new GroupApplicationAssignment { GroupId = groupId, ApplicationSystemId = appId, CreatedAt = DateTime.UtcNow }); await db.SaveChangesAsync();
            var automation = scope.ServiceProvider.GetRequiredService<ILifecycleAutomationService>(); Assert.True((await automation.CreateProfileMappingAsync(new CreateProfileMappingRequest { ApplicationSystemId = appId, SourcePath = "department", TargetAttributeDefinitionId = definitionId, IsAuthoritative = true })).IsSuccess);
            using var expected = JsonDocument.Parse("\"Engineering\""); Assert.True((await automation.CreateDynamicGroupRuleAsync(new CreateDynamicGroupRuleRequest { DirectoryGroupId = groupId, ProfileAttributeDefinitionId = definitionId, ExpectedValue = expected.RootElement.Clone() })).IsSuccess);
        }
        using var admin = await CreateAdminClientAsync(); var token = await ReadDataAsync<ProvisioningTokenResponse>(await admin.PostAsJsonAsync("/api/provisioning-tokens", new CreateProvisioningTokenRequest { ApplicationSystemId = appId, Name = "mapping-test", Scopes = ["scim.users.write", "scim.users.read"], ExpiresAt = DateTime.UtcNow.AddHours(1) })); using var client = _factory.CreateClient(); client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        var response = await client.PostAsJsonAsync("/scim/v2/Users", new { userName = $"mapped-{Guid.NewGuid():N}@example.com", displayName = "Mapped User", active = true, department = "Engineering" }); response.EnsureSuccessStatusCode(); using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); var userId = Guid.Parse(body.RootElement.GetProperty("id").GetString()!);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>(); Assert.True(await db.UserGroupMemberships.AnyAsync(x => x.UserId == userId && x.GroupId == groupId));
            using var value = JsonDocument.Parse("\"Sales\""); var update = await scope.ServiceProvider.GetRequiredService<IUserProfileService>().UpdateUserProfileAsync(userId, new UpdateUserProfileRequest { Attributes = new Dictionary<string, JsonElement?> { [(await db.UserProfileAttributeDefinitions.FindAsync(definitionId))!.Key] = value.RootElement.Clone() } }); Assert.False(update.IsSuccess); Assert.Equal("AUTHORITATIVE_PROFILE_SOURCE", update.ErrorCode);
        }
    }

    private async Task<Guid> GetApplicationIdAsync() { using var scope = _factory.Services.CreateScope(); return await scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>().ApplicationSystems.Where(x => x.Code == DomainConstants.SystemCodes.AuthCenter).Select(x => x.Id).SingleAsync(); }
    private async Task<HttpClient> CreateAdminClientAsync() { var client = _factory.CreateClient(); var auth = await ReadDataAsync<AuthResponse>(await client.PostAsJsonAsync("/api/auth/login", new LoginRequest { Email = AuthCenterWebApplicationFactory.AdminEmail, Password = AuthCenterWebApplicationFactory.AdminPassword, ApplicationCode = DomainConstants.SystemCodes.AuthCenter })); client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken); return client; }
    private static async Task<T> ReadDataAsync<T>(HttpResponseMessage response) where T : class { response.EnsureSuccessStatusCode(); var body = await response.Content.ReadFromJsonAsync<ApiResponse<T>>(); Assert.NotNull(body); Assert.NotNull(body.Data); return body.Data; }
}
