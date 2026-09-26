using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AuthCenter.Contracts.Requests.Auth;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Responses.Auth;
using AuthCenter.Domain.Constants;
using AuthCenter.Domain.Entities;
using AuthCenter.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AuthCenter.IntegrationTests;

public sealed class SecurityRegressionTests : IClassFixture<AuthCenterWebApplicationFactory>
{
    private readonly AuthCenterWebApplicationFactory _factory;

    public SecurityRegressionTests(AuthCenterWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task RepeatedBadPasswords_LockTheAccount()
    {
        var (email, password) = await CreateUserAsync(_factory);
        using var client = _factory.CreateClient();
        var wrongPassword = TestSecretGenerator.CreatePassword();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var failed = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest
            {
                Email = email,
                Password = wrongPassword,
                ApplicationCode = "AUTHCENTER"
            });
            Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
        }

        var locked = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = email,
            Password = password,
            ApplicationCode = "AUTHCENTER"
        });
        Assert.Equal(HttpStatusCode.Unauthorized, locked.StatusCode);
        var body = await locked.Content.ReadFromJsonAsync<ApiResponse<object>>();
        Assert.Equal("ACCOUNT_LOCKED", body?.ErrorCode);
    }

    [Fact]
    public async Task PasswordReset_IgnoresUntrustedCallback_AndQueuesProtectedSafeLink()
    {
        const string safeOrigin = "https://safe.example.com";
        using var factory = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ActionLinks:DefaultBaseUrl"] = safeOrigin
                })));
        var (email, _) = await CreateUserAsync(factory);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/forgot-password", new ForgotPasswordRequest
        {
            Email = email,
            ApplicationCode = "AUTHCENTER",
            CallbackBaseUrl = "https://attacker.example/steal"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
        var message = await db.OutboxMessages.AsNoTracking().OrderByDescending(item => item.CreatedAt).FirstAsync();
        var protector = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("AuthCenter.Outbox.Email.v1");
        var payload = protector.Unprotect(message.ProtectedPayload);

        Assert.Contains(safeOrigin, payload);
        Assert.DoesNotContain("attacker.example", payload);
    }

    [Fact]
    public async Task PermissionOfAnotherApplication_DoesNotOpenTheAuthCenterAdminApi()
    {
        // Permission codes are only unique per application, so another application can hold one
        // that looks exactly like an AuthCenter administration permission. A token issued for that
        // application must not be accepted by AuthCenter's own administration API.
        using var admin = _factory.CreateClient();
        var adminLogin = await admin.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = AuthCenterWebApplicationFactory.AdminEmail,
            Password = AuthCenterWebApplicationFactory.AdminPassword,
            ApplicationCode = "AUTHCENTER"
        });
        var adminToken = (await adminLogin.Content.ReadFromJsonAsync<ApiResponse<AuthResponse>>())!.Data!.AccessToken;
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        var code = "LOOKALIKE" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var created = await admin.PostAsJsonAsync("/api/applications", new { Code = code, Name = "Look-alike " + code, RegistrationMode = "Closed" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var (email, password) = await CreateUserAsync(_factory, code);
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
            var application = await db.ApplicationSystems.SingleAsync(item => item.Code == code);
            var user = await db.Users.SingleAsync(item => item.Email == email);
            var permission = new Permission
            {
                Id = Guid.NewGuid(),
                ApplicationSystemId = application.Id,
                Code = DomainConstants.Permissions.UsersRead,
                Name = "Look-alike permission",
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            };
            var role = new ApplicationRole
            {
                Id = Guid.NewGuid(),
                Name = $"{code}_READER",
                NormalizedName = $"{code}_READER",
                DisplayName = "Look-alike reader",
                ApplicationSystemId = application.Id,
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            };
            db.Permissions.Add(permission);
            db.Roles.Add(role);
            db.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = permission.Id, CreatedAt = DateTime.UtcNow });
            db.UserRoles.Add(new IdentityUserRole<Guid> { UserId = user.Id, RoleId = role.Id });
            await db.SaveChangesAsync();
        }

        using var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest { Email = email, Password = password, ApplicationCode = code });
        var auth = (await login.Content.ReadFromJsonAsync<ApiResponse<AuthResponse>>())!.Data!;
        Assert.Contains(DomainConstants.Permissions.UsersRead, auth.User.Permissions);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/users")).StatusCode);
    }

    [Fact]
    public async Task AuthCenterPermissionCodes_AreReservedForAuthCenter()
    {
        using var admin = _factory.CreateClient();
        var adminLogin = await admin.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = AuthCenterWebApplicationFactory.AdminEmail,
            Password = AuthCenterWebApplicationFactory.AdminPassword,
            ApplicationCode = "AUTHCENTER"
        });
        var adminToken = (await adminLogin.Content.ReadFromJsonAsync<ApiResponse<AuthResponse>>())!.Data!.AccessToken;
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        var code = "RESERVED" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var created = await admin.PostAsJsonAsync("/api/applications", new { Code = code, Name = "Reserved " + code, RegistrationMode = "Closed" });
        using var createdJson = System.Text.Json.JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var applicationId = createdJson.RootElement.GetProperty("data").GetProperty("id").GetGuid();

        var response = await admin.PostAsJsonAsync("/api/permissions", new
        {
            ApplicationSystemId = applicationId,
            Code = DomainConstants.Permissions.UsersWrite,
            Name = "Look-alike"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("RESERVED_PERMISSION_CODE", (await response.Content.ReadFromJsonAsync<ApiResponse<object>>())?.ErrorCode);
    }

    private static async Task<(string Email, string Password)> CreateUserAsync(WebApplicationFactory<Program> factory, string applicationCode = "AUTHCENTER")
    {
        var email = $"security-{Guid.NewGuid():N}@example.com";
        var password = TestSecretGenerator.CreatePassword();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var application = await db.ApplicationSystems.SingleAsync(app => app.Code == applicationCode);
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            FullName = "Security Test",
            Email = email,
            UserName = email,
            EmailConfirmed = true,
            HasLocalPassword = true,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        var result = await userManager.CreateAsync(user, password);
        Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(error => error.Description)));
        db.UserApplicationAccesses.Add(new UserApplicationAccess
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            ApplicationSystemId = application.Id,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        return (email, password);
    }
}
