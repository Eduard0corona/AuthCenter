using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AuthCenter.Contracts.Requests.Auth;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Responses.Auth;
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

    private async Task CreateUserWithApplicationAccessAsync(string email, string password)
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
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }
}
