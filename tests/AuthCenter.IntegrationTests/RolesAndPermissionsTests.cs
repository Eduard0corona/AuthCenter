using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AuthCenter.Contracts.Requests.Auth;
using AuthCenter.Contracts.Requests.Applications;
using AuthCenter.Contracts.Requests.Permissions;
using AuthCenter.Contracts.Requests.Roles;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Responses.Auth;
using AuthCenter.Contracts.Responses.Applications;
using AuthCenter.Contracts.Responses.Permissions;
using AuthCenter.Contracts.Responses.Roles;
using AuthCenter.Domain.Entities;
using AuthCenter.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuthCenter.IntegrationTests;

/// <summary>
/// Covers the roles and permissions controllers, including the permission gate in front of them:
/// these endpoints decide what everyone else in the system is allowed to do.
/// </summary>
public class RolesAndPermissionsTests : IClassFixture<AuthCenterWebApplicationFactory>
{
    private readonly AuthCenterWebApplicationFactory _factory;

    public RolesAndPermissionsTests(AuthCenterWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // --- Roles ---

    [Fact]
    public async Task Role_CanBeCreatedRetrievedUpdatedAndDeactivated()
    {
        using var client = await CreateAdminClientAsync();
        var name = $"Role-{Guid.NewGuid():N}"[..20];

        var created = await ReadDataAsync<RoleDto>(await client.PostAsJsonAsync("/api/roles", new CreateRoleRequest
        {
            Name = name,
            Description = "Created by an integration test",
            ApplicationSystemId = await GetAuthCenterApplicationIdAsync()
        }));
        Assert.Equal(name, created.Name);
        Assert.True(created.IsActive);

        var fetched = await ReadDataAsync<RoleDto>(await client.GetAsync($"/api/roles/{created.Id}"));
        Assert.Equal(created.Id, fetched.Id);
        Assert.Equal("Created by an integration test", fetched.Description);

        var updated = await ReadDataAsync<RoleDto>(await client.PutAsJsonAsync($"/api/roles/{created.Id}",
            new UpdateRoleRequest { Name = name, Description = "Updated description" }));
        Assert.Equal("Updated description", updated.Description);

        Assert.Equal(HttpStatusCode.OK,
            (await client.PatchAsync($"/api/roles/{created.Id}/deactivate", null)).StatusCode);
        Assert.False((await ReadDataAsync<RoleDto>(await client.GetAsync($"/api/roles/{created.Id}"))).IsActive);

        Assert.Equal(HttpStatusCode.OK,
            (await client.PatchAsync($"/api/roles/{created.Id}/activate", null)).StatusCode);
        Assert.True((await ReadDataAsync<RoleDto>(await client.GetAsync($"/api/roles/{created.Id}"))).IsActive);
    }

    [Fact]
    public async Task Role_PermissionsCanBeGrantedAndRemoved()
    {
        using var client = await CreateAdminClientAsync();
        var applicationId = await GetAuthCenterApplicationIdAsync();

        var role = await ReadDataAsync<RoleDto>(await client.PostAsJsonAsync("/api/roles", new CreateRoleRequest
        {
            Name = $"Perm-{Guid.NewGuid():N}"[..20],
            ApplicationSystemId = applicationId
        }));

        var permission = await ReadDataAsync<PermissionDto>(await client.PostAsJsonAsync("/api/permissions",
            new CreatePermissionRequest
            {
                ApplicationSystemId = applicationId,
                Code = $"TEST_{Guid.NewGuid():N}"[..20].ToUpperInvariant(),
                Name = "Test permission"
            }));

        Assert.Equal(HttpStatusCode.OK,
            (await client.PostAsync($"/api/roles/{role.Id}/permissions/{permission.Id}", null)).StatusCode);

        var withPermission = await ReadDataAsync<RoleDto>(await client.GetAsync($"/api/roles/{role.Id}"));
        Assert.Contains(permission.Code, withPermission.Permissions);

        Assert.Equal(HttpStatusCode.OK,
            (await client.DeleteAsync($"/api/roles/{role.Id}/permissions/{permission.Id}")).StatusCode);

        var withoutPermission = await ReadDataAsync<RoleDto>(await client.GetAsync($"/api/roles/{role.Id}"));
        Assert.DoesNotContain(permission.Code, withoutPermission.Permissions);
    }

    [Fact]
    public async Task Role_WithADuplicateName_IsRejected()
    {
        using var client = await CreateAdminClientAsync();
        var request = new CreateRoleRequest
        {
            Name = $"Dup-{Guid.NewGuid():N}"[..20],
            ApplicationSystemId = await GetAuthCenterApplicationIdAsync()
        };

        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/roles", request)).StatusCode);

        var duplicate = await client.PostAsJsonAsync("/api/roles", request);
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
    }

    [Fact]
    public async Task AdministrativeApi_CannotCreateOrMutateSystemRoles()
    {
        using var client = await CreateAdminClientAsync();
        var applicationId = await GetAuthCenterApplicationIdAsync();
        var create = await client.PostAsJsonAsync("/api/roles", new CreateRoleRequest
        {
            Name = "Forged system role",
            ApplicationSystemId = applicationId,
            IsSystemRole = true
        });
        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);

        Guid systemRoleId;
        Guid permissionId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
            systemRoleId = await db.Roles.Where(role => role.IsSystemRole).Select(role => role.Id).FirstAsync();
            permissionId = await db.Permissions.Where(permission => permission.ApplicationSystemId == applicationId).Select(permission => permission.Id).FirstAsync();
        }

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/api/roles/{systemRoleId}", new UpdateRoleRequest { Name = "Changed" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/api/roles/{systemRoleId}/permissions", new SetRolePermissionsRequest { PermissionIds = [permissionId] })).StatusCode);
    }

    [Fact]
    public async Task RolePermissions_AreReplacedAtomicallyAndValidatedByApplication()
    {
        using var client = await CreateAdminClientAsync();
        var applicationId = await GetAuthCenterApplicationIdAsync();
        var role = await ReadDataAsync<RoleDto>(await client.PostAsJsonAsync("/api/roles", new CreateRoleRequest
        {
            Name = $"Matrix-{Guid.NewGuid():N}"[..20],
            ApplicationSystemId = applicationId
        }));
        var permissions = await ReadDataAsync<PagedResult<PermissionDto>>(await client.GetAsync($"/api/applications/{applicationId}/permissions?pageSize=100"));
        var selected = permissions.Items.Take(2).ToArray();

        var updated = await ReadDataAsync<RoleDto>(await client.PutAsJsonAsync($"/api/roles/{role.Id}/permissions", new SetRolePermissionsRequest { PermissionIds = selected.Select(permission => permission.Id).ToArray() }));

        Assert.Equal(selected.Select(permission => permission.Code).Order(), updated.Permissions.Order());
        var idempotent = await ReadDataAsync<RoleDto>(await client.PutAsJsonAsync($"/api/roles/{role.Id}/permissions", new SetRolePermissionsRequest { PermissionIds = selected.Select(permission => permission.Id).ToArray() }));
        Assert.Equal(updated.Permissions.Order(), idempotent.Permissions.Order());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/api/roles/{role.Id}/permissions", new SetRolePermissionsRequest { PermissionIds = [Guid.NewGuid()] })).StatusCode);
        var unchanged = await ReadDataAsync<RoleDto>(await client.GetAsync($"/api/roles/{role.Id}"));
        Assert.Equal(updated.Permissions.Order(), unchanged.Permissions.Order());

        var inactivePermission = selected[0];
        Assert.Equal(HttpStatusCode.OK, (await client.PatchAsync($"/api/permissions/{inactivePermission.Id}/deactivate", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/api/roles/{role.Id}/permissions", new SetRolePermissionsRequest { PermissionIds = [inactivePermission.Id] })).StatusCode);
    }

    [Fact]
    public async Task ApplicationDefaultRole_MustBelongToTheApplicationAndBeActive()
    {
        using var client = await CreateAdminClientAsync();
        var first = await ReadDataAsync<ApplicationDto>(await client.PostAsJsonAsync("/api/applications", new CreateApplicationRequest
        {
            Code = "FIRST_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(), Name = "First", RegistrationMode = "Closed", AllowPasswordLogin = true
        }));
        var second = await ReadDataAsync<ApplicationDto>(await client.PostAsJsonAsync("/api/applications", new CreateApplicationRequest
        {
            Code = "SECOND_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(), Name = "Second", RegistrationMode = "Closed", AllowPasswordLogin = true
        }));
        var foreignRole = await ReadDataAsync<RoleDto>(await client.PostAsJsonAsync("/api/roles", new CreateRoleRequest { Name = "Foreign", ApplicationSystemId = second.Id }));

        var response = await client.PutAsJsonAsync($"/api/applications/{first.Id}", new UpdateApplicationRequest
        {
            Name = first.Name, RegistrationMode = "Closed", AllowPasswordLogin = true, DefaultRoleId = foreignRole.Id
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Role_ThatDoesNotExist_Returns404()
    {
        using var client = await CreateAdminClientAsync();

        var response = await client.GetAsync($"/api/roles/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Roles_AreListedWithPagination()
    {
        using var client = await CreateAdminClientAsync();

        var page = await ReadDataAsync<PagedResult<RoleDto>>(await client.GetAsync("/api/roles?page=1&pageSize=1"));

        Assert.Single(page.Items);
        // SuperAdmin and Admin are seeded, so there is more than one page at this size.
        Assert.True(page.TotalCount >= 2, $"Expected at least the two seeded roles but got {page.TotalCount}.");
    }

    // --- Permissions ---

    [Fact]
    public async Task Permission_CanBeCreatedRetrievedUpdatedAndDeactivated()
    {
        using var client = await CreateAdminClientAsync();
        var applicationId = await GetAuthCenterApplicationIdAsync();
        var code = $"PERM_{Guid.NewGuid():N}"[..20].ToUpperInvariant();

        var created = await ReadDataAsync<PermissionDto>(await client.PostAsJsonAsync("/api/permissions",
            new CreatePermissionRequest
            {
                ApplicationSystemId = applicationId,
                Code = code,
                Name = "Created by an integration test"
            }));
        Assert.Equal(code, created.Code);
        Assert.True(created.IsActive);

        var fetched = await ReadDataAsync<PermissionDto>(await client.GetAsync($"/api/permissions/{created.Id}"));
        Assert.Equal(created.Id, fetched.Id);

        var updated = await ReadDataAsync<PermissionDto>(await client.PutAsJsonAsync($"/api/permissions/{created.Id}",
            new UpdatePermissionRequest { Name = "Updated name", Description = "Updated description" }));
        Assert.Equal("Updated name", updated.Name);

        Assert.Equal(HttpStatusCode.OK,
            (await client.PatchAsync($"/api/permissions/{created.Id}/deactivate", null)).StatusCode);
        Assert.False((await ReadDataAsync<PermissionDto>(
            await client.GetAsync($"/api/permissions/{created.Id}"))).IsActive);

        Assert.Equal(HttpStatusCode.OK,
            (await client.PatchAsync($"/api/permissions/{created.Id}/activate", null)).StatusCode);
        Assert.True((await ReadDataAsync<PermissionDto>(
            await client.GetAsync($"/api/permissions/{created.Id}"))).IsActive);
    }

    [Fact]
    public async Task Permission_WithADuplicateCodeInTheSameApplication_IsRejected()
    {
        using var client = await CreateAdminClientAsync();
        var request = new CreatePermissionRequest
        {
            ApplicationSystemId = await GetAuthCenterApplicationIdAsync(),
            Code = $"DUP_{Guid.NewGuid():N}"[..20].ToUpperInvariant(),
            Name = "Duplicate permission"
        };

        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/permissions", request)).StatusCode);

        var duplicate = await client.PostAsJsonAsync("/api/permissions", request);
        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
    }

    [Fact]
    public async Task Permissions_CanBeListedForOneApplication()
    {
        using var client = await CreateAdminClientAsync();
        var applicationId = await GetAuthCenterApplicationIdAsync();

        var page = await ReadDataAsync<PagedResult<PermissionDto>>(
            await client.GetAsync($"/api/applications/{applicationId}/permissions?pageSize=100"));

        Assert.NotEmpty(page.Items);
        Assert.Contains(page.Items, permission => permission.Code == "AUTHCENTER_USERS_READ");
    }

    // --- Authorization ---

    [Theory]
    [InlineData("/api/roles")]
    [InlineData("/api/permissions")]
    public async Task ReadingWithoutTheRequiredPermission_IsForbidden(string endpoint)
    {
        using var client = await CreateUserWithoutPermissionsClientAsync();

        var response = await client.GetAsync(endpoint);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task WritingWithoutTheRequiredPermission_IsForbidden()
    {
        using var client = await CreateUserWithoutPermissionsClientAsync();

        var response = await client.PostAsJsonAsync("/api/roles", new CreateRoleRequest
        {
            Name = $"Nope-{Guid.NewGuid():N}"[..20]
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/roles")]
    [InlineData("/api/permissions")]
    public async Task ReadingWithoutAuthentication_IsUnauthorized(string endpoint)
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(endpoint);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // --- Helpers ---

    private async Task<HttpClient> CreateAdminClientAsync()
    {
        var client = _factory.CreateClient();
        var auth = await LoginAsync(
            client,
            AuthCenterWebApplicationFactory.AdminEmail,
            AuthCenterWebApplicationFactory.AdminPassword);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return client;
    }

    /// <summary>A user with application access but no roles, so their token carries no permissions.</summary>
    private async Task<HttpClient> CreateUserWithoutPermissionsClientAsync()
    {
        var password = TestSecretGenerator.CreatePassword();
        var email = $"noperms-{Guid.NewGuid():N}@example.com";

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

            var application = await db.ApplicationSystems.SingleAsync(a => a.Code == "AUTHCENTER");
            var user = new ApplicationUser
            {
                Id = Guid.NewGuid(),
                FullName = "No Permissions",
                Email = email,
                UserName = email,
                EmailConfirmed = true,
                HasLocalPassword = true,
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            };

            var result = await userManager.CreateAsync(user, password);
            Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(e => e.Description)));

            db.UserApplicationAccesses.Add(new UserApplicationAccess
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                ApplicationSystemId = application.Id,
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var client = _factory.CreateClient();
        var auth = await LoginAsync(client, email, password);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return client;
    }

    private async Task<Guid> GetAuthCenterApplicationIdAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
        return (await db.ApplicationSystems.AsNoTracking().SingleAsync(a => a.Code == "AUTHCENTER")).Id;
    }

    private static async Task<AuthResponse> LoginAsync(HttpClient client, string email, string password)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = email,
            Password = password,
            ApplicationCode = "AUTHCENTER"
        });

        response.EnsureSuccessStatusCode();
        return await ReadDataAsync<AuthResponse>(response);
    }

    private static async Task<T> ReadDataAsync<T>(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<T>>();
        Assert.NotNull(body);
        Assert.True(body.Success, $"Expected success but got: {body.ErrorCode} — {body.Message}");
        Assert.NotNull(body.Data);
        return body.Data;
    }
}
