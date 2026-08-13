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

public sealed class UserDirectoryDetailTests : IClassFixture<AuthCenterWebApplicationFactory>
{
    private readonly AuthCenterWebApplicationFactory _factory;
    public UserDirectoryDetailTests(AuthCenterWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Detail_DistinguishesDirectAndInheritedAccess()
    {
        using var admin = await CreateAdminClientAsync();
        var applicationId = await GetAuthCenterApplicationIdAsync();
        var user = await CreateUserAsync(admin, grantApplicationAccess: true, applicationId);
        var role = await ReadDataAsync<RoleDto>(await admin.PostAsJsonAsync("/api/roles", new CreateRoleRequest
        {
            Name = $"Directory role {Guid.NewGuid():N}"[..24],
            ApplicationSystemId = applicationId
        }));
        var group = await ReadDataAsync<DirectoryGroupDto>(await admin.PostAsJsonAsync("/api/groups", new CreateDirectoryGroupRequest
        {
            Name = $"Inherited access {Guid.NewGuid():N}"
        }));
        await ReadDataAsync<DirectoryGroupDto>(await admin.PutAsJsonAsync($"/api/groups/{group.Id}/access", new SetDirectoryGroupAccessRequest
        {
            ApplicationSystemIds = [applicationId],
            RoleIds = [role.Id]
        }));
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/groups/{group.Id}/members/{user.Id}", null)).StatusCode);

        var detail = await ReadDataAsync<UserDto>(await admin.GetAsync($"/api/users/{user.Id}"));

        var application = Assert.Single(detail.ApplicationAssignments, assignment => assignment.ApplicationId == applicationId);
        Assert.True(application.IsDirect);
        Assert.True(application.IsEffective);
        Assert.Contains(application.InheritedFromGroups, source => source.GroupId == group.Id && source.IsActive);
        var inheritedRole = Assert.Single(detail.RoleAssignments, assignment => assignment.RoleId == role.Id);
        Assert.False(inheritedRole.IsDirect);
        Assert.True(inheritedRole.IsEffective);
        Assert.Contains(inheritedRole.InheritedFromGroups, source => source.GroupId == group.Id);
        Assert.Contains(detail.GroupMemberships, membership => membership.GroupId == group.Id);
    }

    [Fact]
    public async Task DirectAccess_IsReplacedAtomicallyAndRevokesSessions()
    {
        using var admin = await CreateAdminClientAsync();
        var applicationId = await GetAuthCenterApplicationIdAsync();
        var password = TestSecretGenerator.CreatePassword();
        var user = await CreateUserAsync(admin, grantApplicationAccess: true, applicationId, password);
        var role = await ReadDataAsync<RoleDto>(await admin.PostAsJsonAsync("/api/roles", new CreateRoleRequest
        {
            Name = $"Direct role {Guid.NewGuid():N}"[..24],
            ApplicationSystemId = applicationId
        }));

        var invalid = await admin.PutAsJsonAsync($"/api/users/{user.Id}/access", new SetUserDirectAccessRequest { RoleIds = [role.Id] });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var unchanged = await ReadDataAsync<UserDto>(await admin.GetAsync($"/api/users/{user.Id}"));
        Assert.Contains(unchanged.ApplicationAssignments, assignment => assignment.ApplicationId == applicationId && assignment.IsEffective);

        using var loginClient = _factory.CreateClient();
        var login = await ReadDataAsync<AuthResponse>(await loginClient.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = user.Email,
            Password = password,
            ApplicationCode = DomainConstants.SystemCodes.AuthCenter
        }));
        var updated = await ReadDataAsync<UserDto>(await admin.PutAsJsonAsync($"/api/users/{user.Id}/access", new SetUserDirectAccessRequest
        {
            ApplicationSystemIds = [applicationId],
            RoleIds = [role.Id]
        }));
        Assert.Contains(updated.RoleAssignments, assignment => assignment.RoleId == role.Id && assignment.IsDirect);
        var refresh = await loginClient.PostAsJsonAsync("/api/auth/refresh-token", new RefreshTokenRequest
        {
            RefreshToken = login.RefreshToken,
            ApplicationCode = DomainConstants.SystemCodes.AuthCenter
        });
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }

    [Fact]
    public async Task LastSuperAdmin_CannotLoseAccessRoleOrActiveState()
    {
        using var admin = await CreateAdminClientAsync();
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
        var adminId = await db.Users.Where(user => user.Email == AuthCenterWebApplicationFactory.AdminEmail).Select(user => user.Id).SingleAsync();
        var applicationId = await db.ApplicationSystems.Where(application => application.Code == DomainConstants.SystemCodes.AuthCenter).Select(application => application.Id).SingleAsync();
        var roleId = await db.Roles.Where(role => role.DisplayName == DomainConstants.Roles.SuperAdmin).Select(role => role.Id).SingleAsync();

        await admin.AddReauthenticationProofAsync(AuthCenterWebApplicationFactory.AdminPassword, "admin.super-admin.remove");
        var clear = await admin.PutAsJsonAsync($"/api/users/{adminId}/access", new SetUserDirectAccessRequest());
        Assert.Equal(HttpStatusCode.BadRequest, clear.StatusCode);
        Assert.Equal("LAST_SUPER_ADMIN", (await clear.Content.ReadFromJsonAsync<ApiResponse<object>>())?.ErrorCode);
        await admin.AddReauthenticationProofAsync(AuthCenterWebApplicationFactory.AdminPassword, "admin.super-admin.remove");
        var removeRole = await admin.DeleteAsync($"/api/users/{adminId}/roles/{roleId}");
        Assert.Equal(HttpStatusCode.BadRequest, removeRole.StatusCode);
        Assert.Equal("LAST_SUPER_ADMIN", (await removeRole.Content.ReadFromJsonAsync<ApiResponse<object>>())?.ErrorCode);
        await admin.AddReauthenticationProofAsync(AuthCenterWebApplicationFactory.AdminPassword, "admin.super-admin.remove");
        var deactivate = await admin.PatchAsync($"/api/users/{adminId}/deactivate", null);
        Assert.Equal(HttpStatusCode.BadRequest, deactivate.StatusCode);
        await admin.AddReauthenticationProofAsync(AuthCenterWebApplicationFactory.AdminPassword, "admin.super-admin.remove");
        var revokeApplication = await admin.DeleteAsync($"/api/users/{adminId}/applications/{applicationId}");
        Assert.Equal(HttpStatusCode.BadRequest, revokeApplication.StatusCode);
    }

    [Fact]
    public async Task DestructiveAdministrativeOperations_RequireSingleUseReauthentication()
    {
        using var admin = await CreateAdminClientAsync();
        var applicationId = await GetAuthCenterApplicationIdAsync();
        var user = await CreateUserAsync(admin, grantApplicationAccess: true, applicationId);

        var withoutProof = await admin.DeleteAsync($"/api/users/{user.Id}");
        Assert.Equal(HttpStatusCode.Forbidden, withoutProof.StatusCode);
        Assert.Equal("REAUTHENTICATION_REQUIRED", (await withoutProof.Content.ReadFromJsonAsync<ApiResponse<object>>())?.ErrorCode);

        var proof = await ReadDataAsync<AuthCenter.Contracts.Responses.Auth.ReauthenticationProofResponse>(await admin.PostAsJsonAsync(
            "/api/auth/reauth/password",
            new PasswordReauthenticationRequest { Purpose = "admin.user.delete", Password = AuthCenterWebApplicationFactory.AdminPassword }));
        admin.DefaultRequestHeaders.Add("X-AuthCenter-Reauthentication", proof.ProofToken);
        var deleted = await admin.DeleteAsync($"/api/users/{user.Id}");
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        var reused = await admin.DeleteAsync($"/api/users/{user.Id}");
        Assert.Equal(HttpStatusCode.Forbidden, reused.StatusCode);
    }

    private async Task<UserDto> CreateUserAsync(HttpClient admin, bool grantApplicationAccess, Guid applicationId, string? password = null) =>
        await ReadDataAsync<UserDto>(await admin.PostAsJsonAsync("/api/users", new CreateUserRequest
        {
            FullName = "Directory Detail User",
            Email = $"directory-{Guid.NewGuid():N}@example.com",
            Password = password ?? TestSecretGenerator.CreatePassword(),
            GrantApplicationAccess = grantApplicationAccess,
            ApplicationSystemId = applicationId
        }));

    private async Task<HttpClient> CreateAdminClientAsync()
    {
        var client = _factory.CreateClient();
        var auth = await ReadDataAsync<AuthResponse>(await client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = AuthCenterWebApplicationFactory.AdminEmail,
            Password = AuthCenterWebApplicationFactory.AdminPassword,
            ApplicationCode = DomainConstants.SystemCodes.AuthCenter
        }));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return client;
    }

    private async Task<Guid> GetAuthCenterApplicationIdAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
        return await db.ApplicationSystems.Where(application => application.Code == DomainConstants.SystemCodes.AuthCenter).Select(application => application.Id).SingleAsync();
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
