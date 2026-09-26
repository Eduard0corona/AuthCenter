using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AuthCenter.Contracts.Requests.Auth;
using AuthCenter.Contracts.Requests.OAuth;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Responses.Auth;
using AuthCenter.Domain.Constants;
using AuthCenter.Domain.Enums;
using AuthCenter.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuthCenter.IntegrationTests;

/// <summary>
/// Request contracts are enforced at the API boundary: the registered FluentValidation validators
/// run for every bound argument, so invalid registrations are rejected before any service code.
/// </summary>
public sealed class RequestValidationTests : IClassFixture<AuthCenterWebApplicationFactory>
{
    private readonly AuthCenterWebApplicationFactory _factory;

    public RequestValidationTests(AuthCenterWebApplicationFactory factory) => _factory = factory;

    public static TheoryData<string, CreateOAuthClientRequest> InvalidClients => new()
    {
        { "http redirect", Client(redirectUris: ["http://evil.example/callback"]) },
        { "http login url", Client(loginUrl: "http://evil.example/login") },
        { "password grant", Client(grants: ["authorization_code", "password"]) },
        // Well-formed but unregistered API scopes are rejected by the catalog (ApiResourceTests).
        { "malformed scope", Client(scopes: ["openid", "Not A Scope"]) },
        { "one-year token", Client(lifetime: 31_536_000) },
        { "no pkce", Client(requirePkce: false) },
        { "public machine client", Client(type: OAuthClientType.Public, grants: ["client_credentials"], scopes: ["email"], redirectUris: []) }
    };

    [Theory]
    [MemberData(nameof(InvalidClients))]
    public async Task OAuthClientRegistration_RejectsInsecureOrInvalidContracts(string scenario, CreateOAuthClientRequest template)
    {
        using var admin = await CreateAdminClientAsync();
        var request = new CreateOAuthClientRequest
        {
            ApplicationSystemId = await AuthCenterApplicationIdAsync(),
            ClientId = $"invalid-{Guid.NewGuid():N}"[..24],
            DisplayName = template.DisplayName,
            ClientType = template.ClientType,
            RedirectUris = template.RedirectUris,
            AllowedScopes = template.AllowedScopes,
            GrantTypes = template.GrantTypes,
            LoginUrl = template.LoginUrl,
            AccessTokenLifetimeSeconds = template.AccessTokenLifetimeSeconds,
            RequirePkce = template.RequirePkce
        };

        var response = await admin.PostAsJsonAsync("/api/oauth/clients", request);

        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{scenario}: expected 400, got {(int)response.StatusCode}");
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<object>>();
        Assert.Equal("VALIDATION_FAILED", body?.ErrorCode);
        Assert.NotEmpty(body!.Details!);
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
        Assert.False(await db.OAuthClients.AnyAsync(client => client.ClientId == request.ClientId));
    }

    [Fact]
    public async Task OAuthClientUpdate_RejectsTokenLifetimeAboveOneHour()
    {
        using var admin = await CreateAdminClientAsync();
        var clientId = $"valid-{Guid.NewGuid():N}"[..24];
        var create = await admin.PostAsJsonAsync("/api/oauth/clients", new CreateOAuthClientRequest
        {
            ApplicationSystemId = await AuthCenterApplicationIdAsync(),
            ClientId = clientId,
            DisplayName = "Valid client",
            ClientType = (int)OAuthClientType.Confidential,
            RedirectUris = ["https://app.example.test/callback"],
            AllowedScopes = ["openid", "email"],
            GrantTypes = ["authorization_code"],
            LoginUrl = "https://identity.example.test/login",
            RequirePkce = true
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        var update = await admin.PutAsJsonAsync($"/api/oauth/clients/{clientId}", new UpdateOAuthClientRequest
        {
            DisplayName = "Valid client",
            RedirectUris = ["https://app.example.test/callback"],
            AllowedScopes = ["openid", "email"],
            GrantTypes = ["authorization_code"],
            LoginUrl = "https://identity.example.test/login",
            AccessTokenLifetimeSeconds = 86_400,
            RequirePkce = true
        });

        Assert.Equal(HttpStatusCode.BadRequest, update.StatusCode);
        Assert.Equal("VALIDATION_FAILED", (await update.Content.ReadFromJsonAsync<ApiResponse<object>>())?.ErrorCode);
    }

    [Fact]
    public async Task Login_WithMalformedEmail_IsRejectedBeforeAuthentication()
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = "not-an-email",
            Password = "irrelevant",
            ApplicationCode = DomainConstants.SystemCodes.AuthCenter
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("VALIDATION_FAILED", (await response.Content.ReadFromJsonAsync<ApiResponse<object>>())?.ErrorCode);
    }

    private static CreateOAuthClientRequest Client(
        IList<string>? redirectUris = null,
        string loginUrl = "https://identity.example.test/login",
        IList<string>? grants = null,
        IList<string>? scopes = null,
        int lifetime = 900,
        bool requirePkce = true,
        OAuthClientType type = OAuthClientType.Confidential) => new()
    {
        DisplayName = "Invalid client",
        ClientType = (int)type,
        RedirectUris = redirectUris ?? ["https://app.example.test/callback"],
        AllowedScopes = scopes ?? ["openid", "email"],
        GrantTypes = grants ?? ["authorization_code"],
        LoginUrl = loginUrl,
        AccessTokenLifetimeSeconds = lifetime,
        RequirePkce = requirePkce
    };

    private async Task<HttpClient> CreateAdminClientAsync()
    {
        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = AuthCenterWebApplicationFactory.AdminEmail,
            Password = AuthCenterWebApplicationFactory.AdminPassword,
            ApplicationCode = DomainConstants.SystemCodes.AuthCenter
        });
        login.EnsureSuccessStatusCode();
        var auth = await login.Content.ReadFromJsonAsync<ApiResponse<AuthResponse>>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth!.Data!.AccessToken);
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
}
