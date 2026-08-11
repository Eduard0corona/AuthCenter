using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AuthCenter.Application.Interfaces;
using AuthCenter.Contracts.Requests.Auth;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Responses.Auth;
using AuthCenter.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AuthCenter.IntegrationTests;

public class HardeningTests : IClassFixture<AuthCenterWebApplicationFactory>
{
    private readonly AuthCenterWebApplicationFactory _factory;

    public HardeningTests(AuthCenterWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task LivenessProbe_DoesNotDependOnTheDatabase()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/health/live");

        // A database blip must not fail liveness, or the platform restarts a healthy process.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ReadinessProbe_IsHiddenWithoutAManagementHost()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task RefreshTokenLifetime_FollowsTheConfiguredNumberOfDays()
    {
        const int refreshTokenDays = 3;
        using var factory = new AuthCenterWebApplicationFactory().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Jwt:RefreshTokenDays"] = refreshTokenDays.ToString()
                })));

        using var client = factory.CreateClient();
        var issuedAt = DateTime.UtcNow;
        var auth = await LoginAsync(client);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
        var tokenService = scope.ServiceProvider.GetRequiredService<ITokenService>();
        var hash = tokenService.HashToken(auth.RefreshToken);
        var stored = await db.RefreshTokens.AsNoTracking().SingleAsync(t => t.TokenHash == hash);

        var lifetime = stored.ExpiresAt - issuedAt;
        Assert.True(
            Math.Abs(lifetime.TotalDays - refreshTokenDays) < 0.1,
            $"Expected a {refreshTokenDays} day lifetime but the token lasts {lifetime.TotalDays:F2} days.");
    }

    [Fact]
    public async Task SingleUseMarkers_ArePersistedWhereEveryInstanceCanSeeThem()
    {
        using var client = _factory.CreateClient();
        var auth = await LoginAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        await client.AddReauthenticationProofAsync(AuthCenterWebApplicationFactory.AdminPassword, "factor.enroll");

        // Enrolling in email OTP parks a code that a later request has to read back. If that lived
        // in process memory, a second instance would never find it.
        var setupResponse = await client.PostAsync("/api/auth/mfa/email-otp/setup", null);
        setupResponse.EnsureSuccessStatusCode();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
        var stored = await db.TransientStates.AsNoTracking().ToListAsync();

        var entry = Assert.Single(stored, s => s.Purpose == "emailotp_setup");
        Assert.False(string.IsNullOrWhiteSpace(entry.Value));
        Assert.True(entry.ExpiresAt > entry.CreatedAt);
    }

    private static async Task<AuthResponse> LoginAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = AuthCenterWebApplicationFactory.AdminEmail,
            Password = AuthCenterWebApplicationFactory.AdminPassword,
            ApplicationCode = "AUTHCENTER"
        });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<AuthResponse>>();
        Assert.NotNull(body?.Data);
        return body.Data;
    }
}
