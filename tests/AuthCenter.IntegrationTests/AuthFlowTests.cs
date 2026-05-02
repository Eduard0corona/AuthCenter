using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AuthCenter.Contracts.Requests.Auth;
using AuthCenter.Contracts.Requests.Users;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Responses.Audit;
using AuthCenter.Contracts.Responses.Auth;
using AuthCenter.Contracts.Responses.Users;
using AuthCenter.Domain.Enums;
using AuthCenter.Domain.Entities;
using AuthCenter.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuthCenter.IntegrationTests;

public class AuthFlowTests : IClassFixture<AuthCenterWebApplicationFactory>
{
    private readonly AuthCenterWebApplicationFactory _factory;

    public AuthFlowTests(AuthCenterWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Login_ReturnsAccessToken_AndAuthenticatedUser()
    {
        using var client = _factory.CreateClient();

        var auth = await LoginAsync(client, AuthCenterWebApplicationFactory.AdminEmail, AuthCenterWebApplicationFactory.AdminPassword);

        Assert.False(string.IsNullOrWhiteSpace(auth.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(auth.RefreshToken));
        Assert.Contains("AUTHCENTER", auth.User.Applications);
        Assert.Contains("SuperAdmin", auth.User.Roles);
        Assert.Contains("AUTHCENTER_USERS_READ", auth.User.Permissions);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        var response = await client.GetAsync("/api/auth/me");

        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains(AuthCenterWebApplicationFactory.AdminEmail, json);
    }

    [Fact]
    public async Task RefreshToken_RotatesToken_AndRejectsReusedToken()
    {
        using var client = _factory.CreateClient();
        var auth = await LoginAsync(client, AuthCenterWebApplicationFactory.AdminEmail, AuthCenterWebApplicationFactory.AdminPassword);

        var refreshResponse = await client.PostAsJsonAsync("/api/auth/refresh-token", new RefreshTokenRequest
        {
            RefreshToken = auth.RefreshToken
        });

        refreshResponse.EnsureSuccessStatusCode();
        var refreshed = await ReadAuthResponseAsync(refreshResponse);

        Assert.False(string.IsNullOrWhiteSpace(refreshed.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(refreshed.RefreshToken));
        Assert.NotEqual(auth.RefreshToken, refreshed.RefreshToken);

        var reusedResponse = await client.PostAsJsonAsync("/api/auth/refresh-token", new RefreshTokenRequest
        {
            RefreshToken = auth.RefreshToken
        });

        Assert.Equal(HttpStatusCode.Unauthorized, reusedResponse.StatusCode);
    }

    [Fact]
    public async Task UsersEndpoint_RequiresAuthentication_AndPermission()
    {
        using var client = _factory.CreateClient();

        var anonymousResponse = await client.GetAsync("/api/users");

        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

        var limitedEmail = $"limited-{Guid.NewGuid():N}@example.com";
        const string limitedPassword = "Limited12345";
        await CreateUserWithApplicationAccessAsync(limitedEmail, limitedPassword);

        var limitedAuth = await LoginAsync(client, limitedEmail, limitedPassword);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", limitedAuth.AccessToken);

        var forbiddenResponse = await client.GetAsync("/api/users");

        Assert.Equal(HttpStatusCode.Forbidden, forbiddenResponse.StatusCode);
    }

    [Fact]
    public async Task ApproveAccess_ActivatesPendingApplicationAccess()
    {
        using var client = _factory.CreateClient();
        var adminAuth = await LoginAsync(client, AuthCenterWebApplicationFactory.AdminEmail, AuthCenterWebApplicationFactory.AdminPassword);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminAuth.AccessToken);

        var pendingEmail = $"pending-{Guid.NewGuid():N}@example.com";
        const string pendingPassword = "Pending12345";
        var (userId, applicationId) = await CreateUserWithApplicationAccessAsync(pendingEmail, pendingPassword, isActive: false);

        var pendingLogin = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = pendingEmail,
            Password = pendingPassword,
            ApplicationCode = "AUTHCENTER"
        });
        Assert.Equal(HttpStatusCode.Unauthorized, pendingLogin.StatusCode);

        var approvalResponse = await client.PatchAsync($"/api/users/{userId}/applications/{applicationId}/approve", null);
        approvalResponse.EnsureSuccessStatusCode();

        var approvedAuth = await LoginAsync(client, pendingEmail, pendingPassword);
        Assert.Contains("AUTHCENTER", approvedAuth.User.Applications);

        var userResponse = await client.GetAsync($"/api/users/{userId}");
        userResponse.EnsureSuccessStatusCode();
        var userBody = await userResponse.Content.ReadFromJsonAsync<ApiResponse<UserDto>>();

        Assert.NotNull(userBody?.Data);
        var access = Assert.Single(userBody.Data.ApplicationAccesses);
        Assert.Equal(applicationId, access.ApplicationId);
        Assert.True(access.IsActive);
    }

    [Fact]
    public async Task Login_ScopesRolesAndPermissionsToRequestedApplication()
    {
        using var client = _factory.CreateClient();
        await GrantAdminAccessToSecondaryApplicationAsync();

        var authCenterLogin = await LoginAsync(client, AuthCenterWebApplicationFactory.AdminEmail, AuthCenterWebApplicationFactory.AdminPassword);

        Assert.Contains("AUTHCENTER_USERS_READ", authCenterLogin.User.Permissions);
        Assert.DoesNotContain("SECOND_APP_ADMIN", authCenterLogin.User.Permissions);
        Assert.DoesNotContain("SECOND", authCenterLogin.User.Applications);

        var secondLoginResponse = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = AuthCenterWebApplicationFactory.AdminEmail,
            Password = AuthCenterWebApplicationFactory.AdminPassword,
            ApplicationCode = "SECOND"
        });

        secondLoginResponse.EnsureSuccessStatusCode();
        var secondLogin = await ReadAuthResponseAsync(secondLoginResponse);

        Assert.Contains("SECOND_APP_ADMIN", secondLogin.User.Permissions);
        Assert.DoesNotContain("AUTHCENTER_USERS_READ", secondLogin.User.Permissions);
        Assert.Contains("SECOND", secondLogin.User.Applications);
    }

    [Fact]
    public async Task RefreshToken_RejectsDifferentApplicationCode()
    {
        using var client = _factory.CreateClient();
        await GrantAdminAccessToSecondaryApplicationAsync();
        var auth = await LoginAsync(client, AuthCenterWebApplicationFactory.AdminEmail, AuthCenterWebApplicationFactory.AdminPassword);

        var response = await client.PostAsJsonAsync("/api/auth/refresh-token", new RefreshTokenRequest
        {
            RefreshToken = auth.RefreshToken,
            ApplicationCode = "SECOND"
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Register_EnforcesAllowedDomainsAndEmailConfirmation()
    {
        using var client = _factory.CreateClient();
        await CreateRegistrationApplicationAsync("PORTAL", allowedDomains: "allowed.com", requireEmailConfirmation: true);

        var rejected = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            FullName = "Bad Domain",
            Email = "bad@blocked.com",
            Password = "Password123",
            ApplicationCode = "PORTAL"
        });

        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);

        var accepted = await client.PostAsJsonAsync("/api/auth/register", new RegisterRequest
        {
            FullName = "Good Domain",
            Email = "good@allowed.com",
            Password = "Password123",
            ApplicationCode = "PORTAL"
        });

        Assert.Equal(HttpStatusCode.BadRequest, accepted.StatusCode);
        var body = await accepted.Content.ReadFromJsonAsync<ApiResponse>();
        Assert.Equal("EMAIL_CONFIRMATION_REQUIRED", body?.ErrorCode);

        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = "good@allowed.com",
            Password = "Password123",
            ApplicationCode = "PORTAL"
        });

        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
    }

    [Fact]
    public async Task InviteAndAuditEndpoints_CreateInvitedUserAndExposeAuditLogs()
    {
        using var client = _factory.CreateClient();
        var adminAuth = await LoginAsync(client, AuthCenterWebApplicationFactory.AdminEmail, AuthCenterWebApplicationFactory.AdminPassword);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminAuth.AccessToken);

        var appId = await CreateRegistrationApplicationAsync("INVITES", registrationMode: ApplicationRegistrationMode.InviteOnly);
        var inviteResponse = await client.PostAsJsonAsync("/api/users/invitations", new InviteUserRequest
        {
            FullName = "Invited User",
            Email = "invited@example.com",
            ApplicationSystemId = appId
        });

        inviteResponse.EnsureSuccessStatusCode();
        var inviteBody = await inviteResponse.Content.ReadFromJsonAsync<ApiResponse<UserDto>>();

        Assert.NotNull(inviteBody?.Data);
        Assert.Contains(inviteBody.Data.ApplicationAccesses, a => a.ApplicationId == appId && a.IsActive);

        var auditResponse = await client.GetAsync("/api/audit-logs?action=LOGIN_SUCCESS");
        auditResponse.EnsureSuccessStatusCode();

        var auditBody = await auditResponse.Content.ReadFromJsonAsync<ApiResponse<PagedResult<AuditLogDto>>>();
        Assert.NotNull(auditBody);
        Assert.True(auditBody.Success);
        Assert.NotNull(auditBody.Data);
        Assert.True(auditBody.Data.TotalCount > 0, "Expected at least one LOGIN_SUCCESS audit entry.");
        Assert.All(auditBody.Data.Items, entry => Assert.Equal("LOGIN_SUCCESS", entry.Action));
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
        return await ReadAuthResponseAsync(response);
    }

    private static async Task<AuthResponse> ReadAuthResponseAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<AuthResponse>>();
        Assert.NotNull(body);
        Assert.True(body.Success);
        Assert.NotNull(body.Data);
        return body.Data;
    }

    private async Task<(Guid UserId, Guid ApplicationId)> CreateUserWithApplicationAccessAsync(
        string email,
        string password,
        bool isActive = true)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var app = await db.ApplicationSystems.SingleAsync(a => a.Code == "AUTHCENTER");
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            FullName = "Limited User",
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
            ApplicationSystemId = app.Id,
            IsActive = isActive,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        return (user.Id, app.Id);
    }

    private async Task<Guid> CreateRegistrationApplicationAsync(
        string code,
        string? allowedDomains = null,
        bool requireEmailConfirmation = false,
        ApplicationRegistrationMode registrationMode = ApplicationRegistrationMode.Open)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();

        var existing = await db.ApplicationSystems.FirstOrDefaultAsync(a => a.Code == code);
        if (existing is not null)
            return existing.Id;

        var now = DateTime.UtcNow;
        var app = new ApplicationSystem
        {
            Id = Guid.NewGuid(),
            Code = code,
            Name = code,
            IsActive = true,
            CreatedAt = now,
            RegistrationSettings = new ApplicationRegistrationSettings
            {
                Id = Guid.NewGuid(),
                RegistrationMode = registrationMode,
                AllowGoogleLogin = true,
                AllowPasswordLogin = true,
                RequireEmailConfirmation = requireEmailConfirmation,
                AllowedEmailDomains = allowedDomains,
                CreatedAt = now
            }
        };

        db.ApplicationSystems.Add(app);
        await db.SaveChangesAsync();
        return app.Id;
    }

    private async Task GrantAdminAccessToSecondaryApplicationAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();

        var appId = await CreateRegistrationApplicationAsync("SECOND");
        var admin = await userManager.FindByEmailAsync(AuthCenterWebApplicationFactory.AdminEmail);
        Assert.NotNull(admin);

        if (!await db.UserApplicationAccesses.AnyAsync(a => a.UserId == admin.Id && a.ApplicationSystemId == appId))
        {
            db.UserApplicationAccesses.Add(new UserApplicationAccess
            {
                Id = Guid.NewGuid(),
                UserId = admin.Id,
                ApplicationSystemId = appId,
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            });
        }

        var permission = await db.Permissions.FirstOrDefaultAsync(p => p.Code == "SECOND_APP_ADMIN");
        if (permission is null)
        {
            permission = new Permission
            {
                Id = Guid.NewGuid(),
                ApplicationSystemId = appId,
                Code = "SECOND_APP_ADMIN",
                Name = "Second app admin",
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            };
            db.Permissions.Add(permission);
        }

        var role = await db.Roles.FirstOrDefaultAsync(r => r.Name == "SecondAdmin");
        if (role is null)
        {
            role = new ApplicationRole
            {
                Id = Guid.NewGuid(),
                Name = "SecondAdmin",
                NormalizedName = "SECONDADMIN",
                ApplicationSystemId = appId,
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            };
            var createRole = await roleManager.CreateAsync(role);
            Assert.True(createRole.Succeeded, string.Join(", ", createRole.Errors.Select(e => e.Description)));
        }

        if (!await db.RolePermissions.AnyAsync(rp => rp.RoleId == role.Id && rp.PermissionId == permission.Id))
            db.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = permission.Id, CreatedAt = DateTime.UtcNow });

        await db.SaveChangesAsync();

        if (!await userManager.IsInRoleAsync(admin, "SecondAdmin"))
        {
            var addRole = await userManager.AddToRoleAsync(admin, "SecondAdmin");
            Assert.True(addRole.Succeeded, string.Join(", ", addRole.Errors.Select(e => e.Description)));
        }
    }
}
