using System.Net;
using System.Net.Http.Json;
using AuthCenter.Contracts.Requests.Auth;
using AuthCenter.Contracts.Responses;
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

    private static async Task<(string Email, string Password)> CreateUserAsync(WebApplicationFactory<Program> factory)
    {
        var email = $"security-{Guid.NewGuid():N}@example.com";
        var password = TestSecretGenerator.CreatePassword();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var application = await db.ApplicationSystems.SingleAsync(app => app.Code == "AUTHCENTER");
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
