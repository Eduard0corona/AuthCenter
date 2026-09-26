using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using AuthCenter.Client;
using AuthCenter.Contracts.Requests.Auth;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Responses.Auth;
using AuthCenter.Domain.Constants;
using AuthCenter.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AuthCenter.IntegrationTests;

/// <summary>
/// AuthCenter hosted at an HTTPS authority whose issuer equals its public origin, so the real SDK
/// (OpenID Connect handler, JWT bearer, back-channel HTTP) can run against it end to end.
/// </summary>
public sealed class HttpsAuthCenterFactory : AuthCenterWebApplicationFactory
{
    public const string Authority = "https://authcenter.test";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Issuer"] = Authority,
            ["Oidc:PublicOrigin"] = Authority
        }));
    }

    public HttpClient CreateAuthCenterClient() => CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri(Authority),
        AllowAutoRedirect = false
    });
}

/// <summary>
/// Contract tests that run the shipped AuthCenter.Client SDK against the real server. They exist
/// because unit tests with synthetic claims could not detect contract drift (for example the role
/// claim name) between what AuthCenter issues and what the SDK consumes.
/// </summary>
[Trait("Category", "Conformance")]
public sealed class SdkContractTests : IClassFixture<HttpsAuthCenterFactory>
{
    private const string BffOrigin = "https://bff.test";
    private readonly HttpsAuthCenterFactory _factory;

    public SdkContractTests(HttpsAuthCenterFactory factory) => _factory = factory;

    [Fact]
    public async Task Discovery_AdvertisesTheContractTheSdkRelyOn()
    {
        using var client = _factory.CreateAuthCenterClient();
        using var discovery = JsonDocument.Parse(await client.GetStringAsync("/.well-known/openid-configuration"));
        var root = discovery.RootElement;

        Assert.Equal(HttpsAuthCenterFactory.Authority, root.GetProperty("issuer").GetString());
        Assert.StartsWith(HttpsAuthCenterFactory.Authority, root.GetProperty("jwks_uri").GetString());
        Assert.Contains("role", root.GetProperty("claims_supported").EnumerateArray().Select(item => item.GetString()));
        Assert.False(root.GetProperty("request_uri_parameter_supported").GetBoolean());
        Assert.Contains("S256", root.GetProperty("code_challenge_methods_supported").EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public async Task Bff_SignsIn_ExposesRolesAndPermissions_RefreshesAndSignsOut()
    {
        using var admin = await CreateAdminClientAsync();
        var (clientId, secret) = await RegisterClientAsync(admin, $"{BffOrigin}/signin-authcenter", ["openid", "profile", "email", "offline_access"], ["authorization_code", "refresh_token"]);
        var logs = new List<string>();
        await using var bff = await StartBffAsync(clientId, secret, logs);
        using var browser = new HttpClient(new CookieContainerHandler(new CookieContainer()) { InnerHandler = bff.GetTestServer().CreateHandler() })
        {
            BaseAddress = new Uri(BffOrigin)
        };

        var callback = await SignInThroughBffAsync(browser, admin, "/auth/login?return_url=%2Fwhoami");

        Assert.True(callback.StatusCode == HttpStatusCode.Redirect, string.Join(Environment.NewLine, logs));
        Assert.Equal("/whoami", callback.Headers.Location!.OriginalString);

        var session = await browser.GetAsync("/auth/session");
        Assert.Equal(HttpStatusCode.OK, session.StatusCode);
        using var sessionJson = JsonDocument.Parse(await session.Content.ReadAsStringAsync());
        var user = sessionJson.RootElement.GetProperty("user");
        Assert.Contains(DomainConstants.Roles.SuperAdmin, user.GetProperty("roles").EnumerateArray().Select(item => item.GetString()));
        Assert.Contains(DomainConstants.Permissions.UsersRead, user.GetProperty("permissions").EnumerateArray().Select(item => item.GetString()));
        var csrf = sessionJson.RootElement.GetProperty("csrfToken").GetString()!;

        using var whoami = JsonDocument.Parse(await browser.GetStringAsync("/whoami"));
        Assert.True(whoami.RootElement.GetProperty("isInRole").GetBoolean());
        Assert.True(whoami.RootElement.GetProperty("hasPermission").GetBoolean());

        using var refresh = new HttpRequestMessage(HttpMethod.Post, "/auth/refresh");
        refresh.Headers.Add(AuthCenterBffDefaults.AntiforgeryHeaderName, csrf);
        Assert.Equal(HttpStatusCode.NoContent, (await browser.SendAsync(refresh)).StatusCode);
        using var afterRefresh = JsonDocument.Parse(await browser.GetStringAsync("/whoami"));
        Assert.True(afterRefresh.RootElement.GetProperty("isInRole").GetBoolean());

        using var logout = new HttpRequestMessage(HttpMethod.Post, "/auth/logout");
        logout.Headers.Add(AuthCenterBffDefaults.AntiforgeryHeaderName, csrf);
        Assert.Equal(HttpStatusCode.NoContent, (await browser.SendAsync(logout)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/auth/session")).StatusCode);
    }

    [Fact]
    public async Task Bff_RejectsExternalReturnUrls()
    {
        using var admin = await CreateAdminClientAsync();
        var (clientId, secret) = await RegisterClientAsync(admin, $"{BffOrigin}/signin-authcenter", ["openid", "profile", "email", "offline_access"], ["authorization_code", "refresh_token"]);
        await using var bff = await StartBffAsync(clientId, secret, []);
        using var browser = new HttpClient(new CookieContainerHandler(new CookieContainer()) { InnerHandler = bff.GetTestServer().CreateHandler() })
        {
            BaseAddress = new Uri(BffOrigin)
        };

        var callback = await SignInThroughBffAsync(browser, admin, "/auth/login?return_url=https%3A%2F%2Fevil.example%2F");

        Assert.Equal("/", callback.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task ResourceApi_ValidatesTypeAudienceRolesPermissionsAndScopes()
    {
        using var admin = await CreateAdminClientAsync();
        const string redirect = "https://web.test/callback";
        var (clientId, secret) = await RegisterClientAsync(admin, redirect, ["openid", "profile", "email", "offline_access"], ["authorization_code", "refresh_token"]);
        var sdk = CreateSdkClient(clientId, secret, ["openid", "profile", "email", "offline_access"]);
        var tokens = await AuthorizeWithSdkAsync(sdk, admin, redirect);

        await using var api = await StartApiAsync(clientId);
        using var apiClient = api.GetTestServer().CreateClient();
        apiClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await apiClient.GetAsync("/role")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await apiClient.GetAsync("/permission")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await apiClient.GetAsync("/scope")).StatusCode);

        // An ID token carries the same issuer, audience and algorithm but must not work as a bearer token.
        apiClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.IdToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await apiClient.GetAsync("/role")).StatusCode);

        await using var otherApi = await StartApiAsync("another-api");
        using var otherClient = otherApi.GetTestServer().CreateClient();
        otherClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await otherClient.GetAsync("/role")).StatusCode);
    }

    [Fact]
    public async Task LowLevelClient_RefreshRotatesAndRevocationEndsTheFamily()
    {
        using var admin = await CreateAdminClientAsync();
        const string redirect = "https://worker.test/callback";
        var (clientId, secret) = await RegisterClientAsync(admin, redirect, ["openid", "offline_access"], ["authorization_code", "refresh_token"]);
        var sdk = CreateSdkClient(clientId, secret, ["openid", "offline_access"]);
        var tokens = await AuthorizeWithSdkAsync(sdk, admin, redirect);

        var refreshed = await sdk.RefreshAsync(tokens.RefreshToken!);
        Assert.NotEqual(tokens.RefreshToken, refreshed.RefreshToken);
        Assert.Null(JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(refreshed)).GetProperty("id_token").GetString());

        await sdk.RevokeAsync(refreshed.RefreshToken!);
        await Assert.ThrowsAsync<HttpRequestException>(() => sdk.RefreshAsync(refreshed.RefreshToken!));
    }

    [Fact]
    public async Task ClientCredentials_WithoutExplicitScopes_ReceivesTheClientsMachineScopes()
    {
        using var admin = await CreateAdminClientAsync();
        var (clientId, secret) = await RegisterClientAsync(admin, null, ["email"], ["client_credentials"]);
        var sdk = CreateSdkClient(clientId, secret, []);

        var token = await sdk.ClientCredentialsAsync();

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token.AccessToken);
        Assert.Equal(clientId, jwt.Claims.Single(claim => claim.Type == "client_id").Value);
        Assert.Equal("email", jwt.Claims.Single(claim => claim.Type == "scope").Value);
        Assert.DoesNotContain(jwt.Claims, claim => claim.Type == JwtRegisteredClaimNames.Sub);
    }

    private async Task<HttpResponseMessage> SignInThroughBffAsync(HttpClient browser, HttpClient admin, string loginPath)
    {
        var login = await browser.GetAsync(loginPath);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        using var authCenter = _factory.CreateAuthCenterClient();
        var authorize = await authCenter.GetAsync(login.Headers.Location!);
        Assert.Equal(HttpStatusCode.Redirect, authorize.StatusCode);
        var interactionId = QueryHelpers.ParseQuery(authorize.Headers.Location!.Query)["interaction_id"].ToString();
        // The hosted login authenticates the user; the seeded administrator's first-party token plays that role.
        var complete = await admin.PostAsJsonAsync("/oauth/authorize/complete", new { InteractionId = interactionId, Consent = true });
        Assert.Equal(HttpStatusCode.Redirect, complete.StatusCode);
        return await browser.GetAsync(complete.Headers.Location!);
    }

    private async Task<OAuthTokenSet> AuthorizeWithSdkAsync(AuthCenterClient sdk, HttpClient admin, string redirect)
    {
        var pkce = AuthCenterClient.CreatePkce();
        using var authCenter = _factory.CreateAuthCenterClient();
        var authorize = await authCenter.GetAsync(sdk.BuildAuthorizationUri(new Uri(redirect), "state-value", "nonce-value", pkce));
        Assert.Equal(HttpStatusCode.Redirect, authorize.StatusCode);
        var interactionId = QueryHelpers.ParseQuery(authorize.Headers.Location!.Query)["interaction_id"].ToString();
        var complete = await admin.PostAsJsonAsync("/oauth/authorize/complete", new { InteractionId = interactionId, Consent = true });
        var code = QueryHelpers.ParseQuery(complete.Headers.Location!.Query)["code"].ToString();
        return await sdk.ExchangeCodeAsync(code, new Uri(redirect), pkce.Verifier);
    }

    private AuthCenterClient CreateSdkClient(string clientId, string? secret, IReadOnlyList<string> scopes) => new(
        new HttpClient(_factory.Server.CreateHandler()),
        new AuthCenterClientOptions
        {
            Authority = new Uri(HttpsAuthCenterFactory.Authority),
            ClientId = clientId,
            ClientSecret = secret,
            Scopes = scopes
        });

    private async Task<(string ClientId, string Secret)> RegisterClientAsync(HttpClient admin, string? redirectUri, string[] scopes, string[] grants)
    {
        var clientId = $"sdk-{Guid.NewGuid():N}"[..24];
        var response = await admin.PostAsJsonAsync("/api/oauth/clients", new
        {
            ApplicationSystemId = await AuthCenterApplicationIdAsync(),
            ClientId = clientId,
            DisplayName = "SDK contract client",
            ClientType = 0,
            RedirectUris = redirectUri is null ? Array.Empty<string>() : [redirectUri],
            AllowedScopes = scopes,
            GrantTypes = grants,
            LoginUrl = $"{HttpsAuthCenterFactory.Authority}/login",
            RequirePkce = grants.Contains("authorization_code"),
            AutoConsent = true
        });
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        using var json = JsonDocument.Parse(body);
        return (clientId, json.RootElement.GetProperty("data").GetProperty("clientSecret").GetString()!);
    }

    private async Task<WebApplication> StartBffAsync(string clientId, string secret, List<string> logs)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseTestServer(options => options.BaseAddress = new Uri(BffOrigin));
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new ListLoggerProvider(logs));
        builder.Services.AddAuthCenterBff(new AuthCenterBffOptions
        {
            Authority = new Uri(HttpsAuthCenterFactory.Authority),
            ClientId = clientId,
            ClientSecret = secret
        });
        builder.Services.Configure<OpenIdConnectOptions>(
            AuthCenterBffDefaults.OpenIdConnectScheme,
            options => options.BackchannelHttpHandler = _factory.Server.CreateHandler());
        builder.Services.AddHttpClient<AuthCenterClient>()
            .ConfigurePrimaryHttpMessageHandler(() => _factory.Server.CreateHandler());

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapAuthCenterBff();
        app.MapGet("/whoami", [Authorize(AuthenticationSchemes = AuthCenterBffDefaults.CookieScheme)] (HttpContext context) => Results.Ok(new
        {
            isInRole = context.User.IsInRole(DomainConstants.Roles.SuperAdmin),
            hasPermission = context.User.HasAuthCenterPermission(DomainConstants.Permissions.UsersRead)
        }));
        await app.StartAsync();
        return app;
    }

    private async Task<WebApplication> StartApiAsync(string audience)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication().AddAuthCenterJwtBearer(new Uri(HttpsAuthCenterFactory.Authority), audience);
        builder.Services.Configure<JwtBearerOptions>(
            JwtBearerDefaults.AuthenticationScheme,
            options => options.BackchannelHttpHandler = _factory.Server.CreateHandler());
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy("role", policy => policy.RequireRole(DomainConstants.Roles.SuperAdmin));
            options.AddPolicy("permission", policy => policy.RequireAuthenticatedUser().RequireAuthCenterPermission(DomainConstants.Permissions.UsersRead));
            options.AddPolicy("scope", policy => policy.RequireAuthenticatedUser().RequireAuthCenterScope("openid"));
        });
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/role", () => "ok").RequireAuthorization("role");
        app.MapGet("/permission", () => "ok").RequireAuthorization("permission");
        app.MapGet("/scope", () => "ok").RequireAuthorization("scope");
        await app.StartAsync();
        return app;
    }

    private async Task<HttpClient> CreateAdminClientAsync()
    {
        var client = _factory.CreateAuthCenterClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = AuthCenterWebApplicationFactory.AdminEmail,
            Password = AuthCenterWebApplicationFactory.AdminPassword,
            ApplicationCode = DomainConstants.SystemCodes.AuthCenter
        });
        var auth = (await login.Content.ReadFromJsonAsync<ApiResponse<AuthResponse>>())!.Data!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return client;
    }

    private async Task<Guid> AuthCenterApplicationIdAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
        return await db.ApplicationSystems
            .Where(application => application.Code == DomainConstants.SystemCodes.AuthCenter)
            .Select(application => application.Id)
            .SingleAsync();
    }

    private sealed class ListLoggerProvider(List<string> sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new ListLogger(sink, categoryName);
        public void Dispose() { }

        private sealed class ListLogger(List<string> sink, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (sink) sink.Add($"[{logLevel}] {category}: {formatter(state, exception)} {exception?.Message}");
            }
        }
    }
}
