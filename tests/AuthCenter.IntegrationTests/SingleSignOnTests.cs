using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AuthCenter.Contracts.Requests.Auth;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Responses.Auth;
using AuthCenter.Domain.Constants;
using AuthCenter.Domain.Entities;
using AuthCenter.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OtpNet;

namespace AuthCenter.IntegrationTests;

/// <summary>
/// Single sign-on through /oauth/authorize: an existing hosted-login session answers clients
/// directly, prompt and max_age are honoured, interactions are bound to the browser that started
/// them, and ID tokens describe the session (sid, auth_time, amr, acr).
/// </summary>
public sealed class SingleSignOnTests : IClassFixture<HttpsAuthCenterFactory>
{
    private const string Redirect = "https://rp.test/callback";
    private readonly HttpsAuthCenterFactory _factory;

    public SingleSignOnTests(HttpsAuthCenterFactory factory) => _factory = factory;

    [Fact]
    public async Task PromptNone_WithoutSession_ReturnsLoginRequiredToTheClient()
    {
        var clientId = await RegisterClientAsync(autoConsent: true);
        using var browser = CreateBrowser();

        var response = await browser.GetAsync(AuthorizeUrl(clientId, new() { ["prompt"] = "none" }).Url);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.StartsWith(Redirect, location.OriginalString, StringComparison.Ordinal);
        var query = QueryHelpers.ParseQuery(location.Query);
        Assert.Equal("login_required", query["error"].ToString());
        Assert.Equal("state-value", query["state"].ToString());
        Assert.Equal(HttpsAuthCenterFactory.Authority, query["iss"].ToString());
    }

    [Fact]
    public async Task ExistingSession_AnswersTheClientWithoutTheLoginPage_AndIdTokenDescribesTheSession()
    {
        var clientId = await RegisterClientAsync(autoConsent: true);
        using var browser = CreateBrowser();
        await SignInAsync(browser, AuthCenterWebApplicationFactory.AdminEmail, AuthCenterWebApplicationFactory.AdminPassword);

        var first = await AuthorizeAndExchangeAsync(browser, clientId);
        var second = await AuthorizeAndExchangeAsync(browser, clientId, new() { ["prompt"] = "none" });

        foreach (var idToken in new[] { first, second })
        {
            Assert.Equal(DomainConstants.AuthenticationContextClasses.SingleFactor, idToken.Claims.Single(claim => claim.Type == "acr").Value);
            Assert.Contains(idToken.Claims, claim => claim.Type == "amr" && claim.Value == DomainConstants.AuthenticationMethods.Password);
            Assert.True(Guid.TryParse(idToken.Claims.Single(claim => claim.Type == "sid").Value, out _));
        }

        // Both codes came from the same sign-in: same session and the original authentication time.
        Assert.Equal(first.Claims.Single(claim => claim.Type == "sid").Value, second.Claims.Single(claim => claim.Type == "sid").Value);
        Assert.Equal(first.Claims.Single(claim => claim.Type == "auth_time").Value, second.Claims.Single(claim => claim.Type == "auth_time").Value);
    }

    [Fact]
    public async Task PromptNone_WithoutConsent_ReturnsConsentRequired_AndInteractiveRequestAsksForIt()
    {
        var clientId = await RegisterClientAsync(autoConsent: false);
        using var browser = CreateBrowser();
        await SignInAsync(browser, AuthCenterWebApplicationFactory.AdminEmail, AuthCenterWebApplicationFactory.AdminPassword);

        var silent = await browser.GetAsync(AuthorizeUrl(clientId, new() { ["prompt"] = "none" }).Url);
        Assert.Equal("consent_required", QueryHelpers.ParseQuery(silent.Headers.Location!.Query)["error"].ToString());

        var interactive = await browser.GetAsync(AuthorizeUrl(clientId).Url);
        Assert.StartsWith($"{HttpsAuthCenterFactory.Authority}/login?", interactive.Headers.Location!.OriginalString, StringComparison.Ordinal);
        var interactionId = InteractionId(interactive);
        var interaction = await ReadDataAsync(await browser.GetAsync($"/oauth/interactions/{interactionId}"));
        Assert.True(interaction.GetProperty("requiresConsent").GetBoolean());
        Assert.False(interaction.GetProperty("requiresReauthentication").GetBoolean());
    }

    [Fact]
    public async Task PromptLogin_RequiresAFreshSignIn_ThenCompletesTheSameInteraction()
    {
        var clientId = await RegisterClientAsync(autoConsent: true);
        using var browser = CreateBrowser();
        var csrf = await SignInAsync(browser, AuthCenterWebApplicationFactory.AdminEmail, AuthCenterWebApplicationFactory.AdminPassword);

        var authorize = await browser.GetAsync(AuthorizeUrl(clientId, new() { ["prompt"] = "login" }).Url);
        var interactionId = InteractionId(authorize);
        var context = await ReadDataAsync(await browser.GetAsync($"/oauth/interactions/{interactionId}/context"));
        Assert.True(context.GetProperty("requiresFreshLogin").GetBoolean());

        var stale = await CompleteAsync(browser, csrf, interactionId);
        Assert.Equal(HttpStatusCode.BadRequest, stale.StatusCode);
        Assert.Equal("LOGIN_REQUIRED", (await stale.Content.ReadFromJsonAsync<ApiResponse<object>>())!.ErrorCode);

        csrf = await SignInAsync(browser, AuthCenterWebApplicationFactory.AdminEmail, AuthCenterWebApplicationFactory.AdminPassword, csrf);
        var fresh = await CompleteAsync(browser, csrf, interactionId);
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        var redirectUrl = (await ReadDataAsync(fresh)).GetProperty("redirectUrl").GetString()!;
        Assert.StartsWith(Redirect, redirectUrl, StringComparison.Ordinal);
        Assert.False(string.IsNullOrEmpty(QueryHelpers.ParseQuery(new Uri(redirectUrl).Query)["code"].ToString()));
    }

    [Fact]
    public async Task MaxAgeZero_ForcesTheLoginPage_AndIsSatisfiedByTheSignInThatFollows()
    {
        var clientId = await RegisterClientAsync(autoConsent: true);
        using var browser = CreateBrowser();
        var csrf = await SignInAsync(browser, AuthCenterWebApplicationFactory.AdminEmail, AuthCenterWebApplicationFactory.AdminPassword);

        var authorize = await browser.GetAsync(AuthorizeUrl(clientId, new() { ["max_age"] = "0" }).Url);
        var interactionId = InteractionId(authorize);

        csrf = await SignInAsync(browser, AuthCenterWebApplicationFactory.AdminEmail, AuthCenterWebApplicationFactory.AdminPassword, csrf);
        await Task.Delay(1100);
        var complete = await CompleteAsync(browser, csrf, interactionId);

        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
    }

    [Fact]
    public async Task ReAuthentication_KeepsTheSession_AndMovesAuthTimeForward()
    {
        var clientId = await RegisterClientAsync(autoConsent: true);
        using var browser = CreateBrowser();
        var csrf = await SignInAsync(browser, AuthCenterWebApplicationFactory.AdminEmail, AuthCenterWebApplicationFactory.AdminPassword);
        var before = await AuthorizeAndExchangeAsync(browser, clientId);

        await Task.Delay(1100);
        var (url, verifier) = AuthorizeUrl(clientId, new() { ["prompt"] = "login" });
        var interactionId = InteractionId(await browser.GetAsync(url));
        csrf = await SignInAsync(browser, AuthCenterWebApplicationFactory.AdminEmail, AuthCenterWebApplicationFactory.AdminPassword, csrf);
        var complete = await CompleteAsync(browser, csrf, interactionId);
        var redirectUrl = (await ReadDataAsync(complete)).GetProperty("redirectUrl").GetString()!;
        var after = await ExchangeAsync(clientId, QueryHelpers.ParseQuery(new Uri(redirectUrl).Query)["code"].ToString(), verifier);

        Assert.Equal(Claim(before, "sid"), Claim(after, "sid"));
        Assert.True(long.Parse(Claim(after, "auth_time")) > long.Parse(Claim(before, "auth_time")));
    }

    [Fact]
    public async Task AnotherAccountSigningIn_EndsThePreviousAccountsSession()
    {
        var clientId = await RegisterClientAsync(autoConsent: true);
        using var browser = CreateBrowser();
        var csrf = await SignInAsync(browser, AuthCenterWebApplicationFactory.AdminEmail, AuthCenterWebApplicationFactory.AdminPassword);
        var adminSession = Guid.Parse(Claim(await AuthorizeAndExchangeAsync(browser, clientId), "sid"));

        var email = $"sso-switch-{Guid.NewGuid():N}@example.com";
        var password = TestSecretGenerator.CreatePassword();
        await CreateUserAsync(email, password);
        await SignInAsync(browser, email, password, csrf);
        var other = await AuthorizeAndExchangeAsync(browser, clientId);

        Assert.NotEqual(adminSession, Guid.Parse(Claim(other, "sid")));
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
        Assert.NotNull((await db.RefreshTokens.AsNoTracking().SingleAsync(token => token.Id == adminSession)).RevokedAt);
    }

    [Fact]
    public async Task Interaction_CannotBeContinuedFromAnotherBrowser()
    {
        var clientId = await RegisterClientAsync(autoConsent: true);
        using var victim = CreateBrowser();
        var authorize = await victim.GetAsync(AuthorizeUrl(clientId).Url);
        var interactionId = InteractionId(authorize);

        using var attacker = CreateBrowser();
        var csrf = await SignInAsync(attacker, AuthCenterWebApplicationFactory.AdminEmail, AuthCenterWebApplicationFactory.AdminPassword);

        Assert.Equal(HttpStatusCode.NotFound, (await attacker.GetAsync($"/oauth/interactions/{interactionId}/context")).StatusCode);
        var interaction = await attacker.GetAsync($"/oauth/interactions/{interactionId}");
        Assert.Equal("INTERACTION_BINDING_MISMATCH", (await interaction.Content.ReadFromJsonAsync<ApiResponse<object>>())!.ErrorCode);
        var complete = await CompleteAsync(attacker, csrf, interactionId);
        Assert.Equal("INTERACTION_BINDING_MISMATCH", (await complete.Content.ReadFromJsonAsync<ApiResponse<object>>())!.ErrorCode);

        // The rejected attempts did not consume the interaction: its own browser can still use it.
        Assert.Equal(HttpStatusCode.OK, (await victim.GetAsync($"/oauth/interactions/{interactionId}/context")).StatusCode);
    }

    [Fact]
    public async Task InteractionContext_NamesTheClientsApplication_AndEchoesTheLoginHint()
    {
        using var admin = await CreateAdminClientAsync();
        var code = "SSO" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var created = await admin.PostAsJsonAsync("/api/applications", new { Code = code, Name = "SSO target " + code, RegistrationMode = "Closed" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var clientId = await RegisterClientAsync(autoConsent: true, applicationCode: code);
        using var browser = CreateBrowser();

        var authorize = await browser.GetAsync(AuthorizeUrl(clientId, new() { ["login_hint"] = "someone@example.com" }).Url);
        var context = await ReadDataAsync(await browser.GetAsync($"/oauth/interactions/{InteractionId(authorize)}/context"));

        Assert.Equal(code, context.GetProperty("applicationCode").GetString());
        Assert.Equal("someone@example.com", context.GetProperty("loginHint").GetString());
        Assert.True(context.GetProperty("allowPasswordLogin").GetBoolean());
        Assert.False(context.GetProperty("requiresFreshLogin").GetBoolean());
    }

    [Fact]
    public async Task FormPost_RendersAnAutoSubmittedFormRestrictedToTheClientOrigin()
    {
        var clientId = await RegisterClientAsync(autoConsent: true);
        using var browser = CreateBrowser();
        await SignInAsync(browser, AuthCenterWebApplicationFactory.AdminEmail, AuthCenterWebApplicationFactory.AdminPassword);

        var response = await browser.GetAsync(AuthorizeUrl(clientId, new() { ["response_mode"] = "form_post" }).Url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var csp = response.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("form-action https://rp.test", csp, StringComparison.Ordinal);
        Assert.Contains("default-src 'none'", csp, StringComparison.Ordinal);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("<form method=\"post\" action=\"https://rp.test/callback\">", html, StringComparison.Ordinal);
        Assert.Contains("name=\"code\"", html, StringComparison.Ordinal);
        Assert.Contains("name=\"state\" value=\"state-value\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FormPost_FromTheHostedLogin_IsDeliveredOnceAndOnlyToItsBrowser()
    {
        var clientId = await RegisterClientAsync(autoConsent: true);
        using var browser = CreateBrowser();
        var authorize = await browser.GetAsync(AuthorizeUrl(clientId, new() { ["response_mode"] = "form_post" }).Url);
        var interactionId = InteractionId(authorize);
        var csrf = await SignInAsync(browser, AuthCenterWebApplicationFactory.AdminEmail, AuthCenterWebApplicationFactory.AdminPassword);

        var complete = await CompleteAsync(browser, csrf, interactionId);
        var responsePath = (await ReadDataAsync(complete)).GetProperty("redirectUrl").GetString()!;
        Assert.StartsWith("/oauth/authorize/response/", responsePath, StringComparison.Ordinal);

        using var other = CreateBrowser();
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync(responsePath)).StatusCode);

        // The foreign attempt consumed the one-time response, so even its browser cannot replay it.
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync(responsePath)).StatusCode);
    }

    [Fact]
    public async Task FormPost_FromTheHostedLogin_RendersTheFormForItsBrowser()
    {
        var clientId = await RegisterClientAsync(autoConsent: true);
        using var browser = CreateBrowser();
        var authorize = await browser.GetAsync(AuthorizeUrl(clientId, new() { ["response_mode"] = "form_post" }).Url);
        var interactionId = InteractionId(authorize);
        var csrf = await SignInAsync(browser, AuthCenterWebApplicationFactory.AdminEmail, AuthCenterWebApplicationFactory.AdminPassword);

        var complete = await CompleteAsync(browser, csrf, interactionId);
        var page = await browser.GetAsync((await ReadDataAsync(complete)).GetProperty("redirectUrl").GetString()!);

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("name=\"code\"", await page.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("request_uri", "https://rp.test/request.jwt", "request_uri_not_supported")]
    [InlineData("request", "eyJhbGciOiJub25lIn0.e30.", "request_not_supported")]
    [InlineData("id_token_hint", "not-a-token", "invalid_request")]
    [InlineData("prompt", "none login", "invalid_request")]
    [InlineData("max_age", "-1", "invalid_request")]
    [InlineData("response_mode", "fragment", "invalid_request")]
    public async Task UnsupportedOrInvalidParameters_AreReportedToTheClient(string name, string value, string error)
    {
        var clientId = await RegisterClientAsync(autoConsent: true);
        using var browser = CreateBrowser();

        var response = await browser.GetAsync(AuthorizeUrl(clientId, new() { [name] = value }).Url);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(error, QueryHelpers.ParseQuery(response.Headers.Location!.Query)["error"].ToString());
    }

    [Fact]
    public async Task IdTokenHint_ForAnotherUser_PreventsSilentSignIn()
    {
        var clientId = await RegisterClientAsync(autoConsent: true);
        using var adminBrowser = CreateBrowser();
        await SignInAsync(adminBrowser, AuthCenterWebApplicationFactory.AdminEmail, AuthCenterWebApplicationFactory.AdminPassword);
        var adminIdToken = await AuthorizeAndExchangeAsync(adminBrowser, clientId);

        var email = $"sso-other-{Guid.NewGuid():N}@example.com";
        var password = TestSecretGenerator.CreatePassword();
        await CreateUserAsync(email, password);
        using var otherBrowser = CreateBrowser();
        await SignInAsync(otherBrowser, email, password);

        var response = await otherBrowser.GetAsync(AuthorizeUrl(clientId, new()
        {
            ["prompt"] = "none",
            ["id_token_hint"] = adminIdToken.RawData
        }).Url);

        Assert.Equal("login_required", QueryHelpers.ParseQuery(response.Headers.Location!.Query)["error"].ToString());
    }

    [Fact]
    public async Task SecondFactorSignIn_IsReportedAsMultiFactorInTheIdToken()
    {
        var clientId = await RegisterClientAsync(autoConsent: true);
        var email = $"sso-mfa-{Guid.NewGuid():N}@example.com";
        var password = TestSecretGenerator.CreatePassword();
        await CreateUserAsync(email, password);
        var secret = await EnableTotpAsync(email, password);
        using var browser = CreateBrowser();

        var login = await browser.PostAsJsonAsync("/ui-api/session/login", new LoginRequest
        {
            Email = email,
            Password = password,
            ApplicationCode = DomainConstants.SystemCodes.AuthCenter
        });
        var pending = await ReadDataAsync(login);
        Assert.True(pending.GetProperty("requiresMfa").GetBoolean());
        var verify = await browser.PostAsJsonAsync("/ui-api/session/mfa", new VerifyMfaRequest
        {
            MfaPendingToken = pending.GetProperty("mfaPendingToken").GetString()!,
            TotpCode = new Totp(Base32Encoding.ToBytes(secret)).ComputeTotp(DateTime.UtcNow)
        });
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);

        var idToken = await AuthorizeAndExchangeAsync(browser, clientId);

        Assert.Equal(DomainConstants.AuthenticationContextClasses.MultiFactor, idToken.Claims.Single(claim => claim.Type == "acr").Value);
        var methods = idToken.Claims.Where(claim => claim.Type == "amr").Select(claim => claim.Value).ToList();
        Assert.Contains(DomainConstants.AuthenticationMethods.Password, methods);
        Assert.Contains(DomainConstants.AuthenticationMethods.OneTimePassword, methods);
        Assert.Contains(DomainConstants.AuthenticationMethods.MultiFactor, methods);
    }

    [Fact]
    public async Task Discovery_AdvertisesSingleSignOnCapabilities()
    {
        using var client = _factory.CreateAuthCenterClient();
        using var discovery = JsonDocument.Parse(await client.GetStringAsync("/.well-known/openid-configuration"));
        var root = discovery.RootElement;

        Assert.Contains("form_post", Values(root, "response_modes_supported"));
        Assert.Contains("none", Values(root, "prompt_values_supported"));
        Assert.Contains("login", Values(root, "prompt_values_supported"));
        Assert.Contains(DomainConstants.AuthenticationContextClasses.MultiFactor, Values(root, "acr_values_supported"));
        foreach (var claim in new[] { "sid", "auth_time", "amr", "acr" })
            Assert.Contains(claim, Values(root, "claims_supported"));

        static IEnumerable<string?> Values(JsonElement root, string name) =>
            root.GetProperty(name).EnumerateArray().Select(item => item.GetString());
    }

    private HttpClient CreateBrowser() => _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri(HttpsAuthCenterFactory.Authority),
        AllowAutoRedirect = false,
        HandleCookies = true
    });

    /// <summary>Signs in through the hosted-login API; a browser that already has a session sends its CSRF token.</summary>
    private static async Task<string> SignInAsync(HttpClient browser, string email, string password, string? csrf = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/ui-api/session/login")
        {
            Content = JsonContent.Create(new LoginRequest
            {
                Email = email,
                Password = password,
                ApplicationCode = DomainConstants.SystemCodes.AuthCenter
            })
        };
        if (csrf is not null)
            request.Headers.Add("X-AuthCenter-CSRF", csrf);
        var login = await browser.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return (await ReadDataAsync(login)).GetProperty("csrfToken").GetString()!;
    }

    private static async Task<HttpResponseMessage> CompleteAsync(HttpClient browser, string csrf, string interactionId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/oauth/authorize/complete")
        {
            Content = JsonContent.Create(new { InteractionId = interactionId, Consent = true })
        };
        request.Headers.Add("X-AuthCenter-CSRF", csrf);
        request.Headers.Add("X-AuthCenter-UI", "1");
        return await browser.SendAsync(request);
    }

    private async Task<JwtSecurityToken> AuthorizeAndExchangeAsync(HttpClient browser, string clientId, Dictionary<string, string>? extra = null)
    {
        var (url, verifier) = AuthorizeUrl(clientId, extra);
        var response = await browser.GetAsync(url);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.StartsWith(Redirect, location.OriginalString, StringComparison.Ordinal);
        var code = QueryHelpers.ParseQuery(location.Query)["code"].ToString();
        Assert.False(string.IsNullOrEmpty(code), location.OriginalString);

        return await ExchangeAsync(clientId, code, verifier);
    }

    private async Task<JwtSecurityToken> ExchangeAsync(string clientId, string code, string verifier)
    {
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
        using var json = JsonDocument.Parse(body);
        return new JwtSecurityTokenHandler().ReadJwtToken(json.RootElement.GetProperty("id_token").GetString());
    }

    private static string Claim(JwtSecurityToken token, string type) => token.Claims.Single(claim => claim.Type == type).Value;

    private static (string Url, string Verifier) AuthorizeUrl(string clientId, Dictionary<string, string>? extra = null)
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var parameters = new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = clientId,
            ["redirect_uri"] = Redirect,
            ["scope"] = "openid profile email",
            ["state"] = "state-value",
            ["nonce"] = "nonce-value",
            ["code_challenge"] = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
            ["code_challenge_method"] = "S256"
        };
        foreach (var (key, value) in extra ?? [])
            parameters[key] = value;
        return (QueryHelpers.AddQueryString("/oauth/authorize", parameters), verifier);
    }

    private static string InteractionId(HttpResponseMessage authorize)
    {
        Assert.Equal(HttpStatusCode.Redirect, authorize.StatusCode);
        var interactionId = QueryHelpers.ParseQuery(authorize.Headers.Location!.Query)["interaction_id"].ToString();
        Assert.False(string.IsNullOrEmpty(interactionId), authorize.Headers.Location!.OriginalString);
        return interactionId;
    }

    private async Task<string> RegisterClientAsync(bool autoConsent, string applicationCode = DomainConstants.SystemCodes.AuthCenter)
    {
        using var admin = await CreateAdminClientAsync();
        var clientId = $"sso-{Guid.NewGuid():N}"[..24];
        var response = await admin.PostAsJsonAsync("/api/oauth/clients", new
        {
            ApplicationSystemId = await ApplicationIdAsync(applicationCode),
            ClientId = clientId,
            DisplayName = "SSO test client",
            ClientType = 1,
            RedirectUris = new[] { Redirect },
            AllowedScopes = new[] { "openid", "profile", "email" },
            GrantTypes = new[] { "authorization_code" },
            LoginUrl = $"{HttpsAuthCenterFactory.Authority}/login",
            RequirePkce = true,
            AutoConsent = autoConsent
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

    private async Task<string> EnableTotpAsync(string email, string password)
    {
        using var client = _factory.CreateAuthCenterClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            Email = email,
            Password = password,
            ApplicationCode = DomainConstants.SystemCodes.AuthCenter
        });
        var auth = (await login.Content.ReadFromJsonAsync<ApiResponse<AuthResponse>>())!.Data!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        await client.AddReauthenticationProofAsync(password, "factor.enroll");
        var setup = (await (await client.PostAsync("/api/auth/mfa/setup", null)).Content.ReadFromJsonAsync<ApiResponse<MfaSetupResponse>>())!.Data!;
        var enable = await client.PostAsJsonAsync("/api/auth/mfa/enable", new EnableMfaRequest
        {
            TotpCode = new Totp(Base32Encoding.ToBytes(setup.SecretBase32)).ComputeTotp(DateTime.UtcNow)
        });
        enable.EnsureSuccessStatusCode();
        return setup.SecretBase32;
    }

    private async Task CreateUserAsync(string email, string password)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var application = await db.ApplicationSystems.SingleAsync(item => item.Code == DomainConstants.SystemCodes.AuthCenter);
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            FullName = "Single Sign-On User",
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
            ApplicationSystemId = application.Id,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private async Task<Guid> ApplicationIdAsync(string code)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
        return await db.ApplicationSystems.Where(application => application.Code == code).Select(application => application.Id).SingleAsync();
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
