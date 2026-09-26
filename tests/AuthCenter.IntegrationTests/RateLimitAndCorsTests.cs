using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using AuthCenter.Contracts.Requests.Auth;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Responses.Auth;
using AuthCenter.Domain.Constants;
using AuthCenter.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AuthCenter.IntegrationTests;

/// <summary>AuthCenter with rate limiting enabled, small limits and a configured first-party origin.</summary>
public sealed class RateLimitedAuthCenterFactory : AuthCenterWebApplicationFactory
{
    public const string FirstPartyOrigin = "https://first-party.test";
    private Task<string>? _adminToken;

    /// <summary>One administrator sign-in per fixture: the login endpoint is rate limited per account here.</summary>
    public Task<string> AdminTokenAsync() => _adminToken ??= SignInAdministratorAsync();

    private async Task<string> SignInAdministratorAsync()
    {
        using var client = CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri(HttpsAuthCenterFactory.Authority) });
        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = AdminEmail,
            Password = AdminPassword,
            ApplicationCode = DomainConstants.SystemCodes.AuthCenter
        });
        return (await login.Content.ReadFromJsonAsync<ApiResponse<AuthResponse>>())!.Data!.AccessToken;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Issuer"] = HttpsAuthCenterFactory.Authority,
            ["Oidc:PublicOrigin"] = HttpsAuthCenterFactory.Authority,
            ["Cors:AllowedOrigins:0"] = FirstPartyOrigin,
            ["RateLimiting:Enabled"] = "true",
            ["RateLimiting:Rules:auth-login:0:Dimension"] = "Ip",
            ["RateLimiting:Rules:auth-login:0:PermitLimit"] = "100",
            ["RateLimiting:Rules:auth-login:0:WindowSeconds"] = "60",
            ["RateLimiting:Rules:auth-login:1:Dimension"] = "Account",
            ["RateLimiting:Rules:auth-login:1:PermitLimit"] = "3",
            ["RateLimiting:Rules:auth-login:1:WindowSeconds"] = "900",
            ["RateLimiting:Rules:oauth-token:0:Dimension"] = "Client",
            ["RateLimiting:Rules:oauth-token:0:PermitLimit"] = "5",
            ["RateLimiting:Rules:oauth-token:0:WindowSeconds"] = "60",
            ["RateLimiting:Rules:oauth-token:1:Dimension"] = "AnonymousIp",
            ["RateLimiting:Rules:oauth-token:1:PermitLimit"] = "2",
            ["RateLimiting:Rules:oauth-token:1:WindowSeconds"] = "60"
        }));
    }
}

/// <summary>
/// Rate limits count per account and per OAuth client, not only per address, and CORS is decided
/// per endpoint instead of one global credentialed policy.
/// </summary>
public sealed class RateLimitAndCorsTests : IClassFixture<RateLimitedAuthCenterFactory>
{
    private readonly RateLimitedAuthCenterFactory _factory;

    public RateLimitAndCorsTests(RateLimitedAuthCenterFactory factory) => _factory = factory;

    [Fact]
    public async Task Login_IsLimitedPerAccount_WithoutBlockingOtherAccounts()
    {
        using var client = CreateClient();
        var target = $"target-{Guid.NewGuid():N}@example.com";

        for (var attempt = 0; attempt < 3; attempt++)
            Assert.NotEqual(HttpStatusCode.TooManyRequests, (await LoginAsync(client, target)).StatusCode);
        var limited = await LoginAsync(client, target);

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.RetryAfter?.Delta > TimeSpan.Zero);
        Assert.Equal("RATE_LIMIT_EXCEEDED", (await limited.Content.ReadFromJsonAsync<ApiResponse<object>>())?.ErrorCode);
        // The same address can still sign in to another account; the limit follows the account.
        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await LoginAsync(client, $"other-{Guid.NewGuid():N}@example.com")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await LoginAsync(client, target.ToUpperInvariant())).StatusCode);
    }

    [Fact]
    public async Task TokenEndpoint_CountsPerClient_AndOnlyAnonymousCallsPerAddress()
    {
        var (first, firstSecret) = await CreateMachineClientAsync();
        var (second, secondSecret) = await CreateMachineClientAsync();
        using var client = CreateClient();

        for (var request = 0; request < 5; request++)
            Assert.Equal(HttpStatusCode.OK, (await ClientCredentialsAsync(client, first, firstSecret)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await ClientCredentialsAsync(client, first, firstSecret)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ClientCredentialsAsync(client, second, secondSecret)).StatusCode);

        var anonymous = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "client_credentials" });
        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await client.PostAsync("/oauth/token", anonymous)).StatusCode);
        Assert.NotEqual(HttpStatusCode.TooManyRequests, (await client.PostAsync("/oauth/token", anonymous)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsync("/oauth/token", anonymous)).StatusCode);
    }

    [Fact]
    public async Task TokenEndpoint_AllowsOnlyTheBrowserOriginsRegisteredOnClients_WithoutCredentials()
    {
        var origin = $"https://spa-{Guid.NewGuid():N}".Substring(0, 30) + ".test";
        await CreateSpaClientAsync(origin);
        using var client = CreateClient();

        var allowed = await PreflightAsync(client, "/oauth/token", origin, "POST");
        Assert.Equal(origin, Single(allowed, "Access-Control-Allow-Origin"));
        Assert.False(allowed.Headers.Contains("Access-Control-Allow-Credentials"));

        var unknown = await PreflightAsync(client, "/oauth/token", "https://unknown-spa.test", "POST");
        Assert.False(unknown.Headers.Contains("Access-Control-Allow-Origin"));
        var firstParty = await PreflightAsync(client, "/oauth/token", RateLimitedAuthCenterFactory.FirstPartyOrigin, "POST");
        Assert.False(firstParty.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task CookieSessionEndpoints_AreNeverShared_AndTheFirstPartyApiKeepsItsOrigins()
    {
        using var client = CreateClient();

        using var session = new HttpRequestMessage(HttpMethod.Get, "/ui-api/session");
        session.Headers.Add("Origin", RateLimitedAuthCenterFactory.FirstPartyOrigin);
        Assert.False((await client.SendAsync(session)).Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False((await PreflightAsync(client, "/ui-api/session/logout", RateLimitedAuthCenterFactory.FirstPartyOrigin, "POST")).Headers.Contains("Access-Control-Allow-Origin"));

        var api = await PreflightAsync(client, "/api/users", RateLimitedAuthCenterFactory.FirstPartyOrigin, "GET");
        Assert.Equal(RateLimitedAuthCenterFactory.FirstPartyOrigin, Single(api, "Access-Control-Allow-Origin"));
        Assert.Equal("true", Single(api, "Access-Control-Allow-Credentials"));
        Assert.False((await PreflightAsync(client, "/api/users", "https://evil.test", "GET")).Headers.Contains("Access-Control-Allow-Origin"));

        using var discovery = new HttpRequestMessage(HttpMethod.Get, "/.well-known/openid-configuration");
        discovery.Headers.Add("Origin", "https://any-site.test");
        Assert.Equal("*", Single(await client.SendAsync(discovery), "Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task ClientRegistration_RejectsOriginsWithPathsOrInsecureSchemes()
    {
        using var admin = await CreateAdminClientAsync();
        foreach (var origin in new[] { "https://spa.test/app", "http://spa.test", "https://spa.test?x=1" })
        {
            var response = await admin.PostAsJsonAsync("/api/oauth/clients", SpaClient(await AuthCenterApplicationIdAsync(), origin));
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, origin);
        }
    }

    private HttpClient CreateClient() => _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri(HttpsAuthCenterFactory.Authority),
        AllowAutoRedirect = false
    });

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string email) =>
        client.PostAsJsonAsync("/api/auth/login", new LoginRequest { Email = email, Password = "Wrong-password-1", ApplicationCode = DomainConstants.SystemCodes.AuthCenter });

    private static async Task<HttpResponseMessage> ClientCredentialsAsync(HttpClient client, string clientId, string secret)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/oauth/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "client_credentials" })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{Uri.EscapeDataString(secret)}")));
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> PreflightAsync(HttpClient client, string path, string origin, string method)
    {
        using var request = new HttpRequestMessage(HttpMethod.Options, path);
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", method);
        request.Headers.Add("Access-Control-Request-Headers", "content-type");
        return await client.SendAsync(request);
    }

    private static string Single(HttpResponseMessage response, string header) =>
        Assert.Single(response.Headers.GetValues(header));

    private async Task<(string ClientId, string Secret)> CreateMachineClientAsync()
    {
        using var admin = await CreateAdminClientAsync();
        var response = await admin.PostAsJsonAsync("/api/oauth/clients", new
        {
            ApplicationSystemId = await AuthCenterApplicationIdAsync(),
            ClientId = $"rl-{Guid.NewGuid():N}"[..24],
            DisplayName = "Rate limit machine client",
            ClientType = 0,
            RedirectUris = Array.Empty<string>(),
            AllowedScopes = new[] { "email" },
            GrantTypes = new[] { "client_credentials" },
            LoginUrl = $"{HttpsAuthCenterFactory.Authority}/login",
            RequirePkce = false
        });
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        using var json = System.Text.Json.JsonDocument.Parse(body);
        var data = json.RootElement.GetProperty("data");
        return (data.GetProperty("client").GetProperty("clientId").GetString()!, data.GetProperty("clientSecret").GetString()!);
    }

    private async Task CreateSpaClientAsync(string origin)
    {
        using var admin = await CreateAdminClientAsync();
        var response = await admin.PostAsJsonAsync("/api/oauth/clients", SpaClient(await AuthCenterApplicationIdAsync(), origin));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private static object SpaClient(Guid applicationId, string origin) => new
    {
        ApplicationSystemId = applicationId,
        ClientId = $"spa-{Guid.NewGuid():N}"[..24],
        DisplayName = "Rate limit SPA",
        ClientType = 1,
        RedirectUris = new[] { "https://spa.test/callback" },
        AllowedCorsOrigins = new[] { origin },
        AllowedScopes = new[] { "openid" },
        GrantTypes = new[] { "authorization_code" },
        LoginUrl = $"{HttpsAuthCenterFactory.Authority}/login",
        RequirePkce = true
    };

    private async Task<HttpClient> CreateAdminClientAsync()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await _factory.AdminTokenAsync());
        return client;
    }

    private async Task<Guid> AuthCenterApplicationIdAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
        return await db.ApplicationSystems.Where(application => application.Code == DomainConstants.SystemCodes.AuthCenter).Select(application => application.Id).SingleAsync();
    }
}
