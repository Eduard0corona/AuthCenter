using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AuthCenter.Contracts.Requests.Auth;
using AuthCenter.Contracts.Requests.Applications;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Responses.Applications;
using AuthCenter.Contracts.Responses.Auth;
using AuthCenter.Domain.Constants;
using AuthCenter.Domain.Entities;
using AuthCenter.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuthCenter.IntegrationTests;

public sealed class UserExperienceTests : IClassFixture<AuthCenterWebApplicationFactory>
{
    private readonly AuthCenterWebApplicationFactory _factory;
    public UserExperienceTests(AuthCenterWebApplicationFactory factory) => _factory = factory;

    [Theory]
    [InlineData("/login")]
    [InlineData("/login.html")]
    [InlineData("/portal")]
    [InlineData("/portal.html")]
    [InlineData("/admin-v2/")]
    [InlineData("/admin-v2/event-hooks")]
    public async Task FirstPartyUiDocuments_AlwaysApplyContentSecurityPolicy(string path)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync(path);

        response.EnsureSuccessStatusCode();
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.TryGetValues("Content-Security-Policy", out var values));
        Assert.Contains(values, value => value.Contains("default-src 'self'", StringComparison.Ordinal));
        Assert.Contains(values, value => value.Contains("frame-ancestors 'none'", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("/admin")]
    [InlineData("/admin.html")]
    public async Task RetiredConsole_RedirectsToTheCurrentOne(string path)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/admin-v2/", response.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/assets/admin.js")).StatusCode);
    }

    [Fact]
    public async Task UiSession_UsesSecureCookieAndRejectsMissingCsrf()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
            HandleCookies = true
        });
        var login = await client.PostAsJsonAsync("/ui-api/session/login", new LoginRequest
        {
            Email = AuthCenterWebApplicationFactory.AdminEmail,
            Password = AuthCenterWebApplicationFactory.AdminPassword,
            ApplicationCode = DomainConstants.SystemCodes.AuthCenter
        });
        login.EnsureSuccessStatusCode();
        Assert.Contains(login.Headers.GetValues("Set-Cookie"), value => value.Contains("__Host-AuthCenter.Ui", StringComparison.Ordinal) && value.Contains("HttpOnly", StringComparison.OrdinalIgnoreCase));
        // The double-submit token lasts as long as the session (480 minutes by default), not 30 minutes.
        Assert.Contains(login.Headers.GetValues("Set-Cookie"), value => value.StartsWith("__Host-AuthCenter.Csrf=", StringComparison.Ordinal) && value.Contains("max-age=28800", StringComparison.OrdinalIgnoreCase));

        var current = await client.GetAsync("/ui-api/session");
        current.EnsureSuccessStatusCode();
        var currentBody = await current.Content.ReadFromJsonAsync<ApiResponse<JsonElement>>();
        var csrf = currentBody!.Data.GetProperty("csrfToken").GetString();
        Assert.False(string.IsNullOrWhiteSpace(csrf));

        var rejected = await client.PostAsync("/ui-api/session/logout", null);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        client.DefaultRequestHeaders.Add("X-AuthCenter-CSRF", csrf);
        var logout = await client.PostAsync("/ui-api/session/logout", null);
        logout.EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/ui-api/session")).StatusCode);
    }

    [Fact]
    public async Task Branding_RejectsUnsafeUrlsAndPublishesValidatedTheme()
    {
        var client = await CreateAdminClientAsync();
        var appId = await GetAuthCenterApplicationIdAsync();
        var unsafeResult = await client.PutAsJsonAsync($"/api/applications/{appId}/branding", new UpdateApplicationBrandingRequest
        {
            DisplayName = "AuthCenter Enterprise",
            PrimaryColor = "#123456",
            BackgroundColor = "#F8FAFC",
            LogoUrl = "javascript:alert(1)"
        });
        Assert.Equal(HttpStatusCode.BadRequest, unsafeResult.StatusCode);

        var update = await client.PutAsJsonAsync($"/api/applications/{appId}/branding", new UpdateApplicationBrandingRequest
        {
            DisplayName = "AuthCenter Enterprise",
            PrimaryColor = "#123456",
            BackgroundColor = "#F8FAFC",
            LogoUrl = "https://assets.example.test/logo.svg",
            PrivacyUrl = "https://example.test/privacy"
        });
        update.EnsureSuccessStatusCode();

        client.DefaultRequestHeaders.Authorization = null;
        var published = await client.GetFromJsonAsync<ApiResponse<ApplicationBrandingDto>>("/api/applications/branding/AUTHCENTER");
        Assert.Equal("AuthCenter Enterprise", published!.Data!.DisplayName);
        var css = await client.GetStringAsync("/api/applications/branding/AUTHCENTER/theme.css");
        Assert.Contains("--brand-primary:#123456", css, StringComparison.Ordinal);
        Assert.DoesNotContain("javascript", css, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpdatingApplication_ReturnsExistingBrandingForEditorContinuity()
    {
        var client = await CreateAdminClientAsync();
        var code = "EDITOR_" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        var createdResponse = await client.PostAsJsonAsync("/api/applications", new CreateApplicationRequest
        {
            Code = code,
            Name = "Editor continuity",
            RegistrationMode = "Closed",
            AllowPasswordLogin = true,
            RequireEmailConfirmation = true
        });
        createdResponse.EnsureSuccessStatusCode();
        var created = (await createdResponse.Content.ReadFromJsonAsync<ApiResponse<ApplicationDto>>())!.Data!;

        var brandingResponse = await client.PutAsJsonAsync($"/api/applications/{created.Id}/branding", new UpdateApplicationBrandingRequest
        {
            DisplayName = "Editor continuity branded",
            PrimaryColor = "#175CD3",
            BackgroundColor = "#FFFFFF",
            PrivacyUrl = "https://example.test/privacy",
            TermsUrl = "https://example.test/terms"
        });
        brandingResponse.EnsureSuccessStatusCode();

        var updateResponse = await client.PutAsJsonAsync($"/api/applications/{created.Id}", new UpdateApplicationRequest
        {
            Name = "Editor continuity updated",
            RegistrationMode = "InviteOnly",
            AllowPasswordLogin = true,
            RequireEmailConfirmation = true
        });
        updateResponse.EnsureSuccessStatusCode();
        var updated = (await updateResponse.Content.ReadFromJsonAsync<ApiResponse<ApplicationDto>>())!.Data!;

        Assert.NotNull(updated.Branding);
        Assert.Equal("Editor continuity branded", updated.Branding.DisplayName);
        Assert.Equal("https://example.test/privacy", updated.Branding.PrivacyUrl);
        Assert.Equal("https://example.test/terms", updated.Branding.TermsUrl);
    }

    [Fact]
    public async Task FirstPartyApplication_CannotBeDeactivated()
    {
        var client = await CreateAdminClientAsync();
        var appId = await GetAuthCenterApplicationIdAsync();

        var response = await client.PatchAsync($"/api/applications/{appId}/deactivate", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ApiResponse>();
        Assert.Equal("SYSTEM_APPLICATION_REQUIRED", body!.ErrorCode);
    }

    [Fact]
    public async Task ConsentRevocation_RemovesGrantAndRevokesClientRefreshSessions()
    {
        var client = await CreateAdminClientAsync();
        Guid grantId;
        Guid userId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
            userId = await db.Users.Where(item => item.Email == AuthCenterWebApplicationFactory.AdminEmail).Select(item => item.Id).SingleAsync();
            var appId = await db.ApplicationSystems.Where(item => item.Code == DomainConstants.SystemCodes.AuthCenter).Select(item => item.Id).SingleAsync();
            var oauthClient = new OAuthClient
            {
                Id = Guid.NewGuid(),
                ApplicationSystemId = appId,
                ClientId = "portal-consent-" + Guid.NewGuid().ToString("N"),
                DisplayName = "Portal consent test",
                ClientType = AuthCenter.Domain.Enums.OAuthClientType.Public,
                RedirectUrisJson = "[]",
                AllowedScopesJson = "[\"openid\"]",
                GrantTypesJson = "[\"refresh_token\"]",
                LoginUrl = "https://example.test/login",
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            };
            grantId = Guid.NewGuid();
            db.OAuthClients.Add(oauthClient);
            db.OAuthConsentGrants.Add(new OAuthConsentGrant { Id = grantId, UserId = userId, OAuthClientId = oauthClient.Id, ScopesJson = "[\"openid\"]", GrantedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            db.RefreshTokens.Add(new RefreshToken
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                ApplicationCode = DomainConstants.SystemCodes.AuthCenter,
                OAuthClientId = oauthClient.ClientId,
                TokenHash = Convert.ToBase64String(Guid.NewGuid().ToByteArray()),
                ExpiresAt = DateTime.UtcNow.AddDays(1),
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var response = await client.DeleteAsync($"/oauth/consents/{grantId}");
        response.EnsureSuccessStatusCode();
        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
        Assert.False(await verify.OAuthConsentGrants.AnyAsync(item => item.Id == grantId));
        Assert.All(await verify.RefreshTokens.Where(item => item.UserId == userId && item.OAuthClientId != null && item.OAuthClientId.StartsWith("portal-consent-")).ToListAsync(), item => Assert.NotNull(item.RevokedAt));
    }

    private async Task<HttpClient> CreateAdminClientAsync()
    {
        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = AuthCenterWebApplicationFactory.AdminEmail,
            Password = AuthCenterWebApplicationFactory.AdminPassword,
            ApplicationCode = DomainConstants.SystemCodes.AuthCenter
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<ApiResponse<AuthResponse>>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body!.Data!.AccessToken);
        return client;
    }

    private async Task<Guid> GetAuthCenterApplicationIdAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>().ApplicationSystems
            .Where(item => item.Code == DomainConstants.SystemCodes.AuthCenter).Select(item => item.Id).SingleAsync();
    }
}
