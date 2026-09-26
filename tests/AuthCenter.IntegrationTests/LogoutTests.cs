using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AuthCenter.Application.Interfaces;
using AuthCenter.Contracts.Requests.Auth;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Responses.Auth;
using AuthCenter.Domain.Constants;
using AuthCenter.Domain.Entities;
using AuthCenter.Infrastructure.Persistence;
using AuthCenter.Infrastructure.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuthCenter.IntegrationTests;

/// <summary>
/// Global logout: RP-initiated logout at the end_session_endpoint, the hosted logout page, and
/// the cascade that ends the application grants of a session and notifies its clients.
/// </summary>
public sealed class LogoutTests : IClassFixture<HttpsAuthCenterFactory>
{
    private const string Redirect = "https://logout-rp.test/callback";
    private const string SignedOut = "https://logout-rp.test/signed-out";
    private const string Backchannel = "https://logout-rp.test/backchannel-logout";
    private readonly HttpsAuthCenterFactory _factory;

    public LogoutTests(HttpsAuthCenterFactory factory) => _factory = factory;

    [Fact]
    public async Task IdTokenOfTheCurrentSession_SignsOutAtOnce_EndsItsGrants_AndNotifiesTheClient()
    {
        var client = await RegisterClientAsync();
        var (email, password) = await CreateUserAsync();
        using var browser = CreateBrowser();
        await SignInAsync(browser, email, password);
        var tokens = await AuthorizeAndExchangeAsync(browser, client);
        var idToken = tokens.GetProperty("id_token").GetString()!;
        var sessionId = Guid.Parse(new JwtSecurityTokenHandler().ReadJwtToken(idToken).Claims.Single(claim => claim.Type == "sid").Value);

        var logout = await browser.GetAsync(QueryHelpers.AddQueryString("/oauth/logout", new Dictionary<string, string?>
        {
            ["id_token_hint"] = idToken,
            ["post_logout_redirect_uri"] = SignedOut,
            ["state"] = "after-logout"
        }));

        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        Assert.Equal($"{SignedOut}?state=after-logout", logout.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/ui-api/session")).StatusCode);
        var refresh = await RefreshAsync(client, tokens.GetProperty("refresh_token").GetString()!);
        Assert.Equal(HttpStatusCode.BadRequest, refresh.StatusCode);

        var notification = Assert.Single(await BackchannelNotificationsAsync(sessionId));
        Assert.Equal(client, notification.ClientId);
        await using var scope = _factory.Services.CreateAsyncScope();
        var logoutToken = new JwtSecurityTokenHandler().ReadJwtToken(
            scope.ServiceProvider.GetRequiredService<ITokenService>().GenerateLogoutToken(notification.ClientId, notification.UserId, notification.SessionId));
        Assert.Equal(DomainConstants.Claims.LogoutTokenType, logoutToken.Header.Typ);
        Assert.Equal(client, Assert.Single(logoutToken.Audiences));
        Assert.Equal(sessionId.ToString(), logoutToken.Claims.Single(claim => claim.Type == "sid").Value);
        Assert.Contains(DomainConstants.Claims.BackchannelLogoutEvent, logoutToken.Claims.Single(claim => claim.Type == "events").Value);
        Assert.DoesNotContain(logoutToken.Claims, claim => claim.Type == "nonce");
    }

    [Fact]
    public async Task RequestWithoutIdToken_IsConfirmedOnTheHostedLogoutPage()
    {
        var client = await RegisterClientAsync();
        var (email, password) = await CreateUserAsync();
        using var browser = CreateBrowser();
        var csrf = await SignInAsync(browser, email, password);

        var logout = await browser.GetAsync($"/oauth/logout?client_id={client}&post_logout_redirect_uri={Uri.EscapeDataString(SignedOut)}&state=s1");
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        var page = logout.Headers.Location!.OriginalString;
        Assert.StartsWith("/logout?logout_id=", page, StringComparison.Ordinal);
        var logoutId = QueryHelpers.ParseQuery(new Uri(new Uri(HttpsAuthCenterFactory.Authority), page).Query)["logout_id"].ToString();
        // Still signed in; like the hosted page, take the CSRF token the session endpoint issues.
        csrf = (await ReadDataAsync(await browser.GetAsync("/ui-api/session"))).GetProperty("csrfToken").GetString()!;

        var pending = await ReadDataAsync(await browser.GetAsync($"/oauth/logout/{logoutId}"));
        Assert.Equal("Logout test client", pending.GetProperty("clientDisplayName").GetString());
        using (var other = CreateBrowser())
            Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/oauth/logout/{logoutId}")).StatusCode);

        using var confirm = new HttpRequestMessage(HttpMethod.Post, $"/oauth/logout/{logoutId}/confirm");
        confirm.Headers.Add("X-AuthCenter-CSRF", csrf);
        var confirmed = await ReadDataAsync(await browser.SendAsync(confirm));

        Assert.Equal($"{SignedOut}?state=s1", confirmed.GetProperty("redirectUrl").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/ui-api/session")).StatusCode);
    }

    [Fact]
    public async Task IdTokenOfAnEarlierSession_StillNeedsConfirmation()
    {
        var client = await RegisterClientAsync();
        var (email, password) = await CreateUserAsync();
        using var browser = CreateBrowser();
        var csrf = await SignInAsync(browser, email, password);
        var oldIdToken = (await AuthorizeAndExchangeAsync(browser, client)).GetProperty("id_token").GetString()!;
        using (var hostedLogout = new HttpRequestMessage(HttpMethod.Post, "/ui-api/session/logout"))
        {
            hostedLogout.Headers.Add("X-AuthCenter-CSRF", csrf);
            Assert.Equal(HttpStatusCode.OK, (await browser.SendAsync(hostedLogout)).StatusCode);
        }
        await SignInAsync(browser, email, password);

        var logout = await browser.GetAsync($"/oauth/logout?id_token_hint={oldIdToken}");

        Assert.StartsWith("/logout?logout_id=", logout.Headers.Location!.OriginalString, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/ui-api/session")).StatusCode);
    }

    [Theory]
    [InlineData("client_id={client}&post_logout_redirect_uri=https%3A%2F%2Fevil.example%2F", "INVALID_REQUEST")]
    [InlineData("post_logout_redirect_uri=https%3A%2F%2Flogout-rp.test%2Fsigned-out", "INVALID_REQUEST")]
    [InlineData("id_token_hint=not-a-token", "INVALID_REQUEST")]
    [InlineData("client_id=unknown-client", "INVALID_CLIENT")]
    public async Task InvalidRequests_AreRejectedWithoutRedirecting(string query, string errorCode)
    {
        var client = await RegisterClientAsync();
        using var browser = CreateBrowser();

        var response = await browser.GetAsync($"/oauth/logout?{query.Replace("{client}", client, StringComparison.Ordinal)}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(errorCode, (await response.Content.ReadFromJsonAsync<ApiResponse<object>>())!.ErrorCode);
    }

    [Fact]
    public async Task WithoutASession_TheBrowserGoesStraightToTheRegisteredUri()
    {
        var client = await RegisterClientAsync();
        using var browser = CreateBrowser();

        var response = await browser.GetAsync($"/oauth/logout?client_id={client}&post_logout_redirect_uri={Uri.EscapeDataString(SignedOut)}");

        Assert.Equal(SignedOut, response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task CrossSiteFormPost_ContinuesAsATopLevelGet()
    {
        using var browser = CreateBrowser();

        var response = await browser.PostAsync("/oauth/logout", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = "some-client",
            ["post_logout_redirect_uri"] = SignedOut,
            ["unrelated"] = "dropped"
        }));

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        var location = response.Headers.Location!.OriginalString;
        Assert.StartsWith("/oauth/logout?", location, StringComparison.Ordinal);
        Assert.Contains("client_id=some-client", location, StringComparison.Ordinal);
        Assert.DoesNotContain("unrelated", location, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HostedLogout_AlsoEndsTheApplicationGrantsOfTheSession()
    {
        var client = await RegisterClientAsync();
        var (email, password) = await CreateUserAsync();
        using var browser = CreateBrowser();
        var csrf = await SignInAsync(browser, email, password);
        var tokens = await AuthorizeAndExchangeAsync(browser, client);
        var sessionId = Guid.Parse(new JwtSecurityTokenHandler().ReadJwtToken(tokens.GetProperty("id_token").GetString()).Claims.Single(claim => claim.Type == "sid").Value);

        using var logout = new HttpRequestMessage(HttpMethod.Post, "/ui-api/session/logout");
        logout.Headers.Add("X-AuthCenter-CSRF", csrf);
        Assert.Equal(HttpStatusCode.OK, (await browser.SendAsync(logout)).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await RefreshAsync(client, tokens.GetProperty("refresh_token").GetString()!)).StatusCode);
        Assert.Single(await BackchannelNotificationsAsync(sessionId));
    }

    [Fact]
    public async Task SigningOutEverywhere_NotifiesEveryClientOfEverySession()
    {
        var client = await RegisterClientAsync();
        var (email, password) = await CreateUserAsync();
        using var first = CreateBrowser();
        using var second = CreateBrowser();
        await SignInAsync(first, email, password);
        await SignInAsync(second, email, password);
        var firstSession = SessionOf(await AuthorizeAndExchangeAsync(first, client));
        var secondSession = SessionOf(await AuthorizeAndExchangeAsync(second, client));

        using var api = _factory.CreateAuthCenterClient();
        var login = await api.PostAsJsonAsync("/api/auth/login", new LoginRequest { Email = email, Password = password, ApplicationCode = DomainConstants.SystemCodes.AuthCenter });
        var auth = (await login.Content.ReadFromJsonAsync<ApiResponse<AuthResponse>>())!.Data!;
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        await api.AddReauthenticationProofAsync(password, "session.revoke-all");
        var revoked = await api.DeleteAsync("/api/auth/sessions");
        Assert.True(revoked.IsSuccessStatusCode, await revoked.Content.ReadAsStringAsync());

        Assert.Single(await BackchannelNotificationsAsync(firstSession));
        Assert.Single(await BackchannelNotificationsAsync(secondSession));
    }

    [Fact]
    public async Task Discovery_AdvertisesLogout()
    {
        using var client = _factory.CreateAuthCenterClient();
        using var discovery = JsonDocument.Parse(await client.GetStringAsync("/.well-known/openid-configuration"));
        var root = discovery.RootElement;

        Assert.Equal($"{HttpsAuthCenterFactory.Authority}/oauth/logout", root.GetProperty("end_session_endpoint").GetString());
        Assert.True(root.GetProperty("backchannel_logout_supported").GetBoolean());
        Assert.True(root.GetProperty("backchannel_logout_session_supported").GetBoolean());
        Assert.False(root.GetProperty("frontchannel_logout_supported").GetBoolean());
    }

    [Theory]
    [InlineData("http://rp.example/signed-out", null)]
    [InlineData(null, "https://rp.example/logout#fragment")]
    public async Task ClientRegistration_RejectsUnsafeLogoutUris(string? postLogoutUri, string? backchannelUri)
    {
        using var admin = await CreateAdminClientAsync();
        var response = await admin.PostAsJsonAsync("/api/oauth/clients", new
        {
            ApplicationSystemId = await AuthCenterApplicationIdAsync(),
            ClientId = $"bad-logout-{Guid.NewGuid():N}"[..24],
            DisplayName = "Unsafe logout",
            ClientType = 1,
            RedirectUris = new[] { Redirect },
            PostLogoutRedirectUris = postLogoutUri is null ? Array.Empty<string>() : [postLogoutUri],
            BackchannelLogoutUri = backchannelUri,
            AllowedScopes = new[] { "openid" },
            GrantTypes = new[] { "authorization_code" },
            LoginUrl = $"{HttpsAuthCenterFactory.Authority}/login",
            RequirePkce = true
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static Guid SessionOf(JsonElement tokens) =>
        Guid.Parse(new JwtSecurityTokenHandler().ReadJwtToken(tokens.GetProperty("id_token").GetString()).Claims.Single(claim => claim.Type == "sid").Value);

    private async Task<List<(string ClientId, Guid UserId, Guid SessionId)>> BackchannelNotificationsAsync(Guid sessionId)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
        var queue = scope.ServiceProvider.GetRequiredService<BackchannelLogoutQueue>();
        var messages = await db.OutboxMessages.AsNoTracking().Where(message => message.Type == BackchannelLogoutQueue.MessageType).ToListAsync();
        return messages
            .Select(queue.Read)
            .Where(payload => payload.SessionId == sessionId)
            .Select(payload => (payload.ClientId, payload.UserId, payload.SessionId))
            .ToList();
    }

    private HttpClient CreateBrowser() => _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri(HttpsAuthCenterFactory.Authority),
        AllowAutoRedirect = false,
        HandleCookies = true
    });

    private static async Task<string> SignInAsync(HttpClient browser, string email, string password)
    {
        var login = await browser.PostAsJsonAsync("/ui-api/session/login", new LoginRequest
        {
            Email = email,
            Password = password,
            ApplicationCode = DomainConstants.SystemCodes.AuthCenter
        });
        return (await ReadDataAsync(login)).GetProperty("csrfToken").GetString()!;
    }

    private async Task<JsonElement> AuthorizeAndExchangeAsync(HttpClient browser, string clientId)
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var authorize = await browser.GetAsync(QueryHelpers.AddQueryString("/oauth/authorize", new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = clientId,
            ["redirect_uri"] = Redirect,
            ["scope"] = "openid offline_access",
            ["state"] = "state-value",
            ["nonce"] = "nonce-value",
            ["code_challenge"] = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
            ["code_challenge_method"] = "S256"
        }));
        var code = QueryHelpers.ParseQuery(authorize.Headers.Location!.Query)["code"].ToString();
        Assert.False(string.IsNullOrEmpty(code), authorize.Headers.Location.OriginalString);

        using var backChannel = _factory.CreateAuthCenterClient();
        var token = await backChannel.PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = clientId,
            ["code"] = code,
            ["redirect_uri"] = Redirect,
            ["code_verifier"] = verifier
        }));
        var body = await token.Content.ReadAsStringAsync();
        Assert.True(token.IsSuccessStatusCode, body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task<HttpResponseMessage> RefreshAsync(string clientId, string refreshToken)
    {
        using var backChannel = _factory.CreateAuthCenterClient();
        return await backChannel.PostAsync("/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = clientId,
            ["refresh_token"] = refreshToken
        }));
    }

    private async Task<string> RegisterClientAsync()
    {
        using var admin = await CreateAdminClientAsync();
        var clientId = $"logout-{Guid.NewGuid():N}"[..24];
        var response = await admin.PostAsJsonAsync("/api/oauth/clients", new
        {
            ApplicationSystemId = await AuthCenterApplicationIdAsync(),
            ClientId = clientId,
            DisplayName = "Logout test client",
            ClientType = 1,
            RedirectUris = new[] { Redirect },
            PostLogoutRedirectUris = new[] { SignedOut },
            BackchannelLogoutUri = Backchannel,
            AllowedScopes = new[] { "openid", "offline_access" },
            GrantTypes = new[] { "authorization_code", "refresh_token" },
            LoginUrl = $"{HttpsAuthCenterFactory.Authority}/login",
            RequirePkce = true,
            AutoConsent = true
        });
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        return clientId;
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

    private async Task<(string Email, string Password)> CreateUserAsync()
    {
        var email = $"logout-{Guid.NewGuid():N}@example.com";
        var password = TestSecretGenerator.CreatePassword();
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            FullName = "Logout User",
            Email = email,
            UserName = email,
            EmailConfirmed = true,
            HasLocalPassword = true,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        var result = await users.CreateAsync(user, password);
        Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(error => error.Description)));
        db.UserApplicationAccesses.Add(new UserApplicationAccess
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            ApplicationSystemId = await AuthCenterApplicationIdAsync(),
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        return (email, password);
    }

    private async Task<Guid> AuthCenterApplicationIdAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
        return await db.ApplicationSystems.Where(application => application.Code == DomainConstants.SystemCodes.AuthCenter).Select(application => application.Id).SingleAsync();
    }

    private static async Task<JsonElement> ReadDataAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("data").Clone();
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
