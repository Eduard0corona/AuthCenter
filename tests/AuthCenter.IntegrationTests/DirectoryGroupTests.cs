using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AuthCenter.Contracts.Requests.Auth;
using AuthCenter.Contracts.Requests.Groups;
using AuthCenter.Contracts.Requests.Roles;
using AuthCenter.Contracts.Requests.Users;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Responses.Auth;
using AuthCenter.Contracts.Responses.Groups;
using AuthCenter.Contracts.Responses.Roles;
using AuthCenter.Contracts.Responses.Users;
using AuthCenter.Domain.Constants;
using AuthCenter.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuthCenter.IntegrationTests;

public class DirectoryGroupTests : IClassFixture<AuthCenterWebApplicationFactory>
{
    private readonly AuthCenterWebApplicationFactory _factory;

    public DirectoryGroupTests(AuthCenterWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Group_CanBeManagedAndAudited()
    {
        using var client = await CreateAdminClientAsync();
        var name = $"Engineering-{Guid.NewGuid():N}"[..24];

        var created = await ReadDataAsync<DirectoryGroupDto>(await client.PostAsJsonAsync("/api/groups",
            new CreateDirectoryGroupRequest { Name = name, Description = "Engineering directory group" }));
        Assert.True(created.IsActive);
        Assert.Equal(0, created.MemberCount);

        var duplicate = await client.PostAsJsonAsync("/api/groups",
            new CreateDirectoryGroupRequest { Name = name.ToUpperInvariant() });
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);

        var page = await ReadDataAsync<PagedResult<DirectoryGroupDto>>(
            await client.GetAsync($"/api/groups?search={Uri.EscapeDataString(name[..8])}&pageSize=10"));
        Assert.Contains(page.Items, group => group.Id == created.Id);

        var updated = await ReadDataAsync<DirectoryGroupDto>(await client.PutAsJsonAsync($"/api/groups/{created.Id}",
            new UpdateDirectoryGroupRequest { Name = name, Description = "Updated" }));
        Assert.Equal("Updated", updated.Description);

        Assert.Equal(HttpStatusCode.OK, (await client.PatchAsync($"/api/groups/{created.Id}/deactivate", null)).StatusCode);
        Assert.False((await ReadDataAsync<DirectoryGroupDto>(await client.GetAsync($"/api/groups/{created.Id}"))).IsActive);
        Assert.Equal(HttpStatusCode.OK, (await client.PatchAsync($"/api/groups/{created.Id}/activate", null)).StatusCode);

        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
        Assert.True(await db.AuditLogs.CountAsync(log =>
            log.EntityName == "DirectoryGroup" && log.EntityId == created.Id.ToString()) >= 4);
    }

    [Fact]
    public async Task Group_AssignmentGrantsApplicationRoleAndRevokesSessionsWhenChanged()
    {
        using var admin = await CreateAdminClientAsync();
        var applicationId = await GetAuthCenterApplicationIdAsync();
        var adminRoleId = await GetAdminRoleIdAsync();
        var password = TestSecretGenerator.CreatePassword();
        var email = $"group-user-{Guid.NewGuid():N}@example.com";
        var user = await ReadDataAsync<UserDto>(await admin.PostAsJsonAsync("/api/users", new CreateUserRequest
        {
            FullName = "Group User",
            Email = email,
            Password = password,
            GrantApplicationAccess = false
        }));
        var group = await ReadDataAsync<DirectoryGroupDto>(await admin.PostAsJsonAsync("/api/groups",
            new CreateDirectoryGroupRequest { Name = $"AuthCenter admins {Guid.NewGuid():N}" }));

        await ReadDataAsync<DirectoryGroupDto>(await admin.PutAsJsonAsync($"/api/groups/{group.Id}/access",
            new SetDirectoryGroupAccessRequest { ApplicationSystemIds = [applicationId], RoleIds = [adminRoleId] }));
        Assert.Equal(HttpStatusCode.OK,
            (await admin.PostAsync($"/api/groups/{group.Id}/members/{user.Id}", null)).StatusCode);

        using var loginClient = _factory.CreateClient();
        var login = await ReadDataAsync<AuthResponse>(await loginClient.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = email,
            Password = password,
            ApplicationCode = DomainConstants.SystemCodes.AuthCenter
        }));
        Assert.Contains(DomainConstants.Roles.Admin, login.User.Roles);
        Assert.Contains(DomainConstants.SystemCodes.AuthCenter, login.User.Applications);
        Assert.Contains(DomainConstants.Permissions.GroupsRead, login.User.Permissions);

        var members = await ReadDataAsync<PagedResult<DirectoryGroupMemberDto>>(
            await admin.GetAsync($"/api/groups/{group.Id}/members"));
        Assert.Contains(members.Items, member => member.UserId == user.Id);

        await ReadDataAsync<DirectoryGroupDto>(await admin.PutAsJsonAsync($"/api/groups/{group.Id}/access",
            new SetDirectoryGroupAccessRequest { ApplicationSystemIds = [applicationId] }));
        var revokedRefresh = await loginClient.PostAsJsonAsync("/api/auth/refresh-token", new RefreshTokenRequest
        {
            RefreshToken = login.RefreshToken,
            ApplicationCode = DomainConstants.SystemCodes.AuthCenter
        });
        Assert.Equal(HttpStatusCode.Unauthorized, revokedRefresh.StatusCode);

        var withoutGroupRole = await ReadDataAsync<AuthResponse>(await loginClient.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = email,
            Password = password,
            ApplicationCode = DomainConstants.SystemCodes.AuthCenter
        }));
        Assert.DoesNotContain(DomainConstants.Roles.Admin, withoutGroupRole.User.Roles);
        Assert.DoesNotContain(DomainConstants.Permissions.GroupsRead, withoutGroupRole.User.Permissions);

        await ReadDataAsync<DirectoryGroupDto>(await admin.PutAsJsonAsync($"/api/groups/{group.Id}/access",
            new SetDirectoryGroupAccessRequest()));
        var denied = await loginClient.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = email,
            Password = password,
            ApplicationCode = DomainConstants.SystemCodes.AuthCenter
        });
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
    }

    [Fact]
    public async Task Group_RoleRequiresApplicationAssignment()
    {
        using var admin = await CreateAdminClientAsync();
        var group = await ReadDataAsync<DirectoryGroupDto>(await admin.PostAsJsonAsync("/api/groups",
            new CreateDirectoryGroupRequest { Name = $"No application {Guid.NewGuid():N}" }));

        var response = await admin.PostAsync($"/api/groups/{group.Id}/roles/{await GetAdminRoleIdAsync()}", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<object>>();
        Assert.Equal("GROUP_APP_ACCESS_REQUIRED", body?.ErrorCode);
    }

    [Fact]
    public async Task GroupAccess_IsReplacedAtomicallyAndRejectsInvalidCombinations()
    {
        using var admin = await CreateAdminClientAsync();
        var applicationId = await GetAuthCenterApplicationIdAsync();
        var group = await ReadDataAsync<DirectoryGroupDto>(await admin.PostAsJsonAsync("/api/groups",
            new CreateDirectoryGroupRequest { Name = $"Atomic access {Guid.NewGuid():N}" }));
        var role = await ReadDataAsync<RoleDto>(await admin.PostAsJsonAsync("/api/roles", new CreateRoleRequest
        {
            Name = $"Group role {Guid.NewGuid():N}"[..24],
            ApplicationSystemId = applicationId
        }));

        var invalid = await admin.PutAsJsonAsync($"/api/groups/{group.Id}/access", new SetDirectoryGroupAccessRequest
        {
            RoleIds = [role.Id]
        });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Empty((await ReadDataAsync<DirectoryGroupDto>(await admin.GetAsync($"/api/groups/{group.Id}"))).Roles);

        var updated = await ReadDataAsync<DirectoryGroupDto>(await admin.PutAsJsonAsync($"/api/groups/{group.Id}/access", new SetDirectoryGroupAccessRequest
        {
            ApplicationSystemIds = [applicationId],
            RoleIds = [role.Id]
        }));
        Assert.Contains(updated.Applications, application => application.Id == applicationId);
        Assert.Contains(updated.Roles, assignedRole => assignedRole.Id == role.Id);

        var idempotent = await ReadDataAsync<DirectoryGroupDto>(await admin.PutAsJsonAsync($"/api/groups/{group.Id}/access", new SetDirectoryGroupAccessRequest
        {
            ApplicationSystemIds = [applicationId],
            RoleIds = [role.Id]
        }));
        Assert.Single(idempotent.Applications);
        Assert.Single(idempotent.Roles);
    }

    [Fact]
    public async Task GroupAccessReplacement_RemovesRolesWhenApplicationIsRemoved()
    {
        using var admin = await CreateAdminClientAsync();
        var applicationId = await GetAuthCenterApplicationIdAsync();
        var group = await ReadDataAsync<DirectoryGroupDto>(await admin.PostAsJsonAsync("/api/groups", new CreateDirectoryGroupRequest { Name = $"Remove access {Guid.NewGuid():N}" }));
        var roleId = await GetAdminRoleIdAsync();
        await ReadDataAsync<DirectoryGroupDto>(await admin.PutAsJsonAsync($"/api/groups/{group.Id}/access", new SetDirectoryGroupAccessRequest { ApplicationSystemIds = [applicationId], RoleIds = [roleId] }));

        var cleared = await ReadDataAsync<DirectoryGroupDto>(await admin.PutAsJsonAsync($"/api/groups/{group.Id}/access", new SetDirectoryGroupAccessRequest()));

        Assert.Empty(cleared.Applications);
        Assert.Empty(cleared.Roles);
    }

    [Fact]
    public async Task GroupAccess_RejectsSuperAdminBecausePrivilegedAccessMustBeDirect()
    {
        using var admin = await CreateAdminClientAsync();
        var applicationId = await GetAuthCenterApplicationIdAsync();
        var group = await ReadDataAsync<DirectoryGroupDto>(await admin.PostAsJsonAsync("/api/groups",
            new CreateDirectoryGroupRequest { Name = $"No inherited super admin {Guid.NewGuid():N}" }));

        var response = await admin.PutAsJsonAsync($"/api/groups/{group.Id}/access", new SetDirectoryGroupAccessRequest
        {
            ApplicationSystemIds = [applicationId],
            RoleIds = [await GetSuperAdminRoleIdAsync()]
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<object>>();
        Assert.Equal("SUPER_ADMIN_GROUP_ASSIGNMENT_FORBIDDEN", body?.ErrorCode);

        var legacyAssignment = await admin.PostAsync(
            $"/api/groups/{group.Id}/roles/{await GetSuperAdminRoleIdAsync()}", null);
        Assert.Equal(HttpStatusCode.BadRequest, legacyAssignment.StatusCode);
        var legacyBody = await legacyAssignment.Content.ReadFromJsonAsync<ApiResponse<object>>();
        Assert.Equal("SUPER_ADMIN_GROUP_ASSIGNMENT_FORBIDDEN", legacyBody?.ErrorCode);
    }

    private async Task<HttpClient> CreateAdminClientAsync()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = AuthCenterWebApplicationFactory.AdminEmail,
            Password = AuthCenterWebApplicationFactory.AdminPassword,
            ApplicationCode = DomainConstants.SystemCodes.AuthCenter
        });
        var auth = await ReadDataAsync<AuthResponse>(response);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return client;
    }

    private async Task<Guid> GetAuthCenterApplicationIdAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
        return await db.ApplicationSystems
            .Where(application => application.Code == DomainConstants.SystemCodes.AuthCenter)
            .Select(application => application.Id)
            .SingleAsync();
    }

    private async Task<Guid> GetAdminRoleIdAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
        return await db.Roles
            .Where(role => role.DisplayName == DomainConstants.Roles.Admin)
            .Select(role => role.Id)
            .SingleAsync();
    }

    private async Task<Guid> GetSuperAdminRoleIdAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
        return await db.Roles
            .Where(role => role.DisplayName == DomainConstants.Roles.SuperAdmin)
            .Select(role => role.Id)
            .SingleAsync();
    }

    private static async Task<T> ReadDataAsync<T>(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<T>>();
        Assert.NotNull(body);
        Assert.True(body.Success, $"Expected success but got {body.ErrorCode}: {body.Message}");
        Assert.NotNull(body.Data);
        return body.Data;
    }
}
