using System.Net.Http.Headers;
using System.Net.Http.Json;
using AuthCenter.Application.Interfaces;
using AuthCenter.Contracts.Requests.Auth;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Responses.Auth;
using AuthCenter.Contracts.Responses.Users;
using AuthCenter.Domain.Constants;
using AuthCenter.Domain.Entities;
using AuthCenter.Domain.Enums;
using AuthCenter.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuthCenter.IntegrationTests;

/// <summary>The contracts the admin console's overview and user filters rely on.</summary>
public sealed class AdminConsoleContractTests : IClassFixture<AuthCenterWebApplicationFactory>
{
    private readonly AuthCenterWebApplicationFactory _factory;

    public AdminConsoleContractTests(AuthCenterWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Dashboard_CountsRejectedSignIns_AndPendingAccessRequests()
    {
        using var admin = await CreateAdminClientAsync();
        var before = await ReadDataAsync<AdminDashboardDto>(await admin.GetAsync("/api/admin-dashboard"));

        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var audit = scope.ServiceProvider.GetRequiredService<IAuditService>();
            await audit.LogAsync("LOGIN_LOCKED_OUT");
            await audit.LogAsync("MFA_VERIFY_FAILED");
            await audit.LogAsync("PASSKEY_LOGIN_FAILED");
            await audit.LogAsync("LOGIN_SUCCESS");
        }
        var prefix = $"pending{Guid.NewGuid():N}"[..16];
        var pending = await CreateUserWithAccessAsync($"{prefix}-a@example.com", isActive: false, revoked: false);
        await CreateUserWithAccessAsync($"{prefix}-b@example.com", isActive: false, revoked: true);
        await CreateUserWithAccessAsync($"{prefix}-c@example.com", isActive: true, revoked: false);

        var after = await ReadDataAsync<AdminDashboardDto>(await admin.GetAsync("/api/admin-dashboard"));

        Assert.Equal(before.FailedLoginsLast24Hours + 3, after.FailedLoginsLast24Hours);
        Assert.Equal(before.PendingAccessRequests + 1, after.PendingAccessRequests);

        // A revoked access is not waiting for approval.
        var pendingUsers = await ReadDataAsync<PagedResult<UserDto>>(await admin.GetAsync($"/api/users?search={prefix}&hasPendingAccess=true"));
        Assert.Equal([pending], pendingUsers.Items.Select(user => user.Id).ToArray());
        var settledUsers = await ReadDataAsync<PagedResult<UserDto>>(await admin.GetAsync($"/api/users?search={prefix}&hasPendingAccess=false"));
        Assert.Equal(2, settledUsers.TotalCount);
        Assert.DoesNotContain(settledUsers.Items, user => user.Id == pending);
    }

    [Fact]
    public async Task Metadata_NamesTheEnvironment()
    {
        using var admin = await CreateAdminClientAsync();

        var metadata = await ReadDataAsync<AdminApiMetadataDto>(await admin.GetAsync("/api/admin-metadata"));

        Assert.False(string.IsNullOrWhiteSpace(metadata.EnvironmentName));
        Assert.Equal(DomainConstants.Permissions.EventHooksWrite, metadata.OperationPermissions["POST /api/event-hooks/{id}/rotate-secret"]);
    }

    private async Task<Guid> CreateUserWithAccessAsync(string email, bool isActive, bool revoked)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            FullName = email,
            Email = email,
            UserName = email,
            EmailConfirmed = true,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        Assert.True((await users.CreateAsync(user)).Succeeded);
        var applicationId = await db.ApplicationSystems.Where(application => application.Code == DomainConstants.SystemCodes.AuthCenter).Select(application => application.Id).SingleAsync();
        db.UserApplicationAccesses.Add(new UserApplicationAccess
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            ApplicationSystemId = applicationId,
            IsActive = isActive,
            CreatedAt = DateTime.UtcNow,
            RevokedAt = revoked ? DateTime.UtcNow : null
        });
        // Pending access always waits on the request its owners decide (the services record both).
        if (!isActive && !revoked)
        {
            db.AccessRequests.Add(new AccessRequest
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                ApplicationSystemId = applicationId,
                Source = AccessRequestSource.Administrator,
                Status = AccessRequestStatus.Pending,
                CreatedAt = DateTime.UtcNow
            });
        }
        await db.SaveChangesAsync();
        return user.Id;
    }

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

    private static async Task<T> ReadDataAsync<T>(HttpResponseMessage response) where T : class
    {
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<T>>();
        Assert.NotNull(body?.Data);
        return body.Data;
    }
}
