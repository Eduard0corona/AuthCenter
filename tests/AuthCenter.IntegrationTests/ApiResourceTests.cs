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

namespace AuthCenter.IntegrationTests;

/// <summary>
/// APIs as OAuth resources (RFC 8707): the catalog, one API audience per token with the roles and
/// permissions of the API's application, token exchange (RFC 8693) and introspection (RFC 7662).
/// </summary>
public sealed class ApiResourceTests : IClassFixture<HttpsAuthCenterFactory>
{
    private const string Redirect = "https://api-rp.test/callback";
    private readonly HttpsAuthCenterFactory _factory;

    public ApiResourceTests(HttpsAuthCenterFactory factory) => _factory = factory;

    [Fact]
    public async Task Catalog_KeepsIdentifiersAndScopeNamesUnique()
    {
        using var admin = await CreateAdminClientAsync();
        var application = await CreateApplicationAsync(admin);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var identifier = $"https://catalog-{suffix}.test/api";

        var created = await admin.PostAsJsonAsync("/api/api-resources", ApiRequest(application.Id, identifier, $"catalog{suffix}.read"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await ReadDataAsync(created)).GetProperty("id").GetGuid();

        Assert.Equal("API_IDENTIFIER_TAKEN", await ErrorCodeAsync(await admin.PostAsJsonAsync("/api/api-resources", ApiRequest(application.Id, identifier, $"other{suffix}.read"))));
        Assert.Equal("API_SCOPE_TAKEN", await ErrorCodeAsync(await admin.PostAsJsonAsync("/api/api-resources", ApiRequest(application.Id, $"{identifier}/v2", $"catalog{suffix}.read"))));
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/api-resources", ApiRequest(application.Id, "orders-api", $"plain{suffix}.read"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/api-resources", ApiRequest(application.Id, $"{identifier}/v3", "openid"))).StatusCode);

        var updated = await admin.PutAsJsonAsync($"/api/api-resources/{id}", new
        {
            DisplayName = "Catalog API",
            IsActive = true,
            Scopes = new[] { new { Name = $"catalog{suffix}.read", DisplayName = "Read" }, new { Name = $"catalog{suffix}.write", DisplayName = "Write" } }
        });
        var scopes = (await ReadDataAsync(updated)).GetProperty("scopes").EnumerateArray().Select(scope => scope.GetProperty("name").GetString()).ToList();
        Assert.Equal([$"catalog{suffix}.read", $"catalog{suffix}.write"], scopes);

        Assert.Equal("UNKNOWN_SCOPE", await ErrorCodeAsync(await admin.PostAsJsonAsync("/api/oauth/clients", ClientRequest(application.Id, ["openid", "unknown.scope"], public_: true))));
    }

    [Fact]
    public async Task AccessTokenForAnApi_HasItsAudience_AndThePermissionsOfItsApplication()
    {
        var world = await CreateWorldAsync();
        using var browser = CreateBrowser();
        await SignInAsync(browser, world.Email, world.Password);

        var tokens = await AuthorizeAndExchangeAsync(browser, world.WebClient, $"openid {world.OrdersScope}");

        var access = new JwtSecurityTokenHandler().ReadJwtToken(tokens.GetProperty("access_token").GetString());
        Assert.Equal([world.OrdersApi, DomainConstants.OAuthAudiences.UserInfo], access.Audiences.ToArray());
        Assert.Equal($"openid {world.OrdersScope}", access.Claims.Single(claim => claim.Type == "scope").Value);
        Assert.Contains(access.Claims, claim => claim.Type == DomainConstants.Claims.Permissions && claim.Value == world.OrdersPermission);
        Assert.Equal(world.OrdersApplicationCode, access.Claims.Single(claim => claim.Type == DomainConstants.Claims.Applications).Value);
        Assert.Equal($"openid {world.OrdersScope}", tokens.GetProperty("scope").GetString());

        // The same token still reaches UserInfo, as an OpenID Connect client does after sign-in.
        using var userInfo = _factory.CreateAuthCenterClient();
        userInfo.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("access_token").GetString());
        Assert.Equal(HttpStatusCode.OK, (await userInfo.GetAsync("/oauth/userinfo")).StatusCode);
    }

    [Fact]
    public async Task ResourceIndicators_MustMatchTheRequestedApiScopes()
    {
        var world = await CreateWorldAsync();
        using var browser = CreateBrowser();
        await SignInAsync(browser, world.Email, world.Password);

        Assert.Equal("invalid_target", await AuthorizeErrorAsync(browser, world.WebClient, $"openid {world.OrdersScope}", [world.BillingApi]));
        Assert.Equal("invalid_target", await AuthorizeErrorAsync(browser, world.WebClient, "openid", [world.OrdersApi]));
        Assert.Equal("invalid_target", await AuthorizeErrorAsync(browser, world.WebClient, $"openid {world.OrdersScope}", ["not a uri"]));
        Assert.Null(await AuthorizeErrorAsync(browser, world.WebClient, $"openid {world.OrdersScope}", [world.OrdersApi]));
    }

    [Fact]
    public async Task GrantForTwoApis_IssuesOneAudiencePerToken_AndRefreshCanSwitchApi()
    {
        var world = await CreateWorldAsync();
        using var browser = CreateBrowser();
        await SignInAsync(browser, world.Email, world.Password);
        var (url, verifier) = AuthorizeUrl(world.WebClient, $"openid offline_access {world.OrdersScope} {world.BillingScope}");
        var code = QueryHelpers.ParseQuery((await browser.GetAsync(url)).Headers.Location!.Query)["code"].ToString();

        var ambiguous = await TokenAsync(new() { ["grant_type"] = "authorization_code", ["client_id"] = world.WebClient, ["code"] = code, ["redirect_uri"] = Redirect, ["code_verifier"] = verifier });
        Assert.Equal("invalid_target", await OAuthErrorAsync(ambiguous));

        var orders = await TokenJsonAsync(new() { ["grant_type"] = "authorization_code", ["client_id"] = world.WebClient, ["code"] = code, ["redirect_uri"] = Redirect, ["code_verifier"] = verifier, ["resource"] = world.OrdersApi });
        var ordersToken = new JwtSecurityTokenHandler().ReadJwtToken(orders.GetProperty("access_token").GetString());
        Assert.Contains(world.OrdersApi, ordersToken.Audiences);
        Assert.DoesNotContain(world.BillingScope, ordersToken.Claims.Single(claim => claim.Type == "scope").Value);

        var billing = await TokenJsonAsync(new() { ["grant_type"] = "refresh_token", ["client_id"] = world.WebClient, ["refresh_token"] = orders.GetProperty("refresh_token").GetString()!, ["resource"] = world.BillingApi });
        var billingToken = new JwtSecurityTokenHandler().ReadJwtToken(billing.GetProperty("access_token").GetString());
        Assert.Contains(world.BillingApi, billingToken.Audiences);
        Assert.Equal($"openid offline_access {world.BillingScope}", billing.GetProperty("scope").GetString());

        var unknown = await TokenAsync(new() { ["grant_type"] = "refresh_token", ["client_id"] = world.WebClient, ["refresh_token"] = billing.GetProperty("refresh_token").GetString()!, ["resource"] = "https://unknown.test" });
        Assert.Equal("invalid_target", await OAuthErrorAsync(unknown));
    }

    [Fact]
    public async Task UserWithoutAccessToTheApisApplication_IsDenied()
    {
        var world = await CreateWorldAsync(grantOrdersAccess: false);
        using var browser = CreateBrowser();
        await SignInAsync(browser, world.Email, world.Password);

        Assert.Equal("access_denied", await AuthorizeErrorAsync(browser, world.WebClient, $"openid {world.OrdersScope}", []));
    }

    [Fact]
    public async Task ClientCredentials_ForAnApi_UseTheApiAudience()
    {
        var world = await CreateWorldAsync();

        var token = await TokenJsonAsync(new() { ["grant_type"] = "client_credentials", ["client_id"] = world.ServiceClient, ["client_secret"] = world.ServiceSecret, ["scope"] = world.OrdersScope });

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token.GetProperty("access_token").GetString());
        Assert.Equal([world.OrdersApi], jwt.Audiences.ToArray());
        Assert.Equal(world.OrdersScope, jwt.Claims.Single(claim => claim.Type == "scope").Value);
        Assert.DoesNotContain(jwt.Claims, claim => claim.Type == JwtRegisteredClaimNames.Sub);
    }

    [Fact]
    public async Task TokenExchange_LetsAnApiCallAnotherApiOnBehalfOfTheUser()
    {
        var world = await CreateWorldAsync();
        using var browser = CreateBrowser();
        await SignInAsync(browser, world.Email, world.Password);
        var userToken = (await AuthorizeAndExchangeAsync(browser, world.WebClient, $"openid {world.OrdersScope}")).GetProperty("access_token").GetString()!;

        var exchanged = await TokenJsonAsync(ExchangeRequest(world.OrdersApiClient, world.OrdersApiSecret, userToken, world.BillingApi));

        Assert.Equal(DomainConstants.OAuthTokenTypes.AccessToken, exchanged.GetProperty("issued_token_type").GetString());
        var subject = new JwtSecurityTokenHandler().ReadJwtToken(userToken);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(exchanged.GetProperty("access_token").GetString());
        Assert.Equal([world.BillingApi], jwt.Audiences.ToArray());
        Assert.Equal(subject.Subject, jwt.Subject);
        Assert.Equal(world.BillingScope, jwt.Claims.Single(claim => claim.Type == "scope").Value);
        Assert.Contains(world.OrdersApiClient, jwt.Claims.Single(claim => claim.Type == "act").Value);
        Assert.True(jwt.ValidTo <= subject.ValidTo);
        // The delegated token describes the user's original authentication, as the subject token does.
        Assert.Equal(
            [DomainConstants.AuthenticationMethods.Password],
            jwt.Claims.Where(claim => claim.Type == "amr").Select(claim => claim.Value).ToArray());
        Assert.Equal(subject.Claims.Single(claim => claim.Type == "acr").Value, jwt.Claims.Single(claim => claim.Type == "acr").Value);

        // Only the API the user's token was issued for can exchange it.
        Assert.Equal("invalid_grant", await OAuthErrorAsync(await TokenAsync(ExchangeRequest(world.BillingApiClient, world.BillingApiSecret, userToken, world.OrdersApi))));
        Assert.Equal("unauthorized_client", await OAuthErrorAsync(await TokenAsync(ExchangeRequest(world.ServiceClient, world.ServiceSecret, userToken, world.BillingApi))));
    }

    [Fact]
    public async Task Introspection_ShowsTokensOnlyToTheirApis_AndReflectsSignOut()
    {
        var world = await CreateWorldAsync();
        using var browser = CreateBrowser();
        var csrf = await SignInAsync(browser, world.Email, world.Password);
        var tokens = await AuthorizeAndExchangeAsync(browser, world.WebClient, $"openid offline_access {world.OrdersScope}");
        var accessToken = tokens.GetProperty("access_token").GetString()!;

        var active = await IntrospectAsync(world.OrdersApiClient, world.OrdersApiSecret, accessToken);
        Assert.True(active.GetProperty("active").GetBoolean());
        Assert.Equal($"openid offline_access {world.OrdersScope}", active.GetProperty("scope").GetString());
        Assert.Equal(world.WebClient, active.GetProperty("client_id").GetString());
        Assert.False((await IntrospectAsync(world.BillingApiClient, world.BillingApiSecret, accessToken)).GetProperty("active").GetBoolean());
        Assert.False((await IntrospectAsync(world.OrdersApiClient, world.OrdersApiSecret, "not-a-token")).GetProperty("active").GetBoolean());

        using var logout = new HttpRequestMessage(HttpMethod.Post, "/ui-api/session/logout");
        logout.Headers.Add("X-AuthCenter-CSRF", csrf);
        Assert.Equal(HttpStatusCode.OK, (await browser.SendAsync(logout)).StatusCode);

        Assert.False((await IntrospectAsync(world.OrdersApiClient, world.OrdersApiSecret, accessToken)).GetProperty("active").GetBoolean());
        using var anonymous = _factory.CreateAuthCenterClient();
        var unauthenticated = await anonymous.PostAsync("/oauth/introspect", new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = accessToken, ["client_id"] = world.WebClient }));
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
    }

    [Fact]
    public async Task Discovery_AdvertisesIntrospectionAndTokenExchange()
    {
        using var client = _factory.CreateAuthCenterClient();
        using var discovery = JsonDocument.Parse(await client.GetStringAsync("/.well-known/openid-configuration"));
        var root = discovery.RootElement;

        Assert.Equal($"{HttpsAuthCenterFactory.Authority}/oauth/introspect", root.GetProperty("introspection_endpoint").GetString());
        Assert.Contains(DomainConstants.OAuthGrantTypes.TokenExchange, root.GetProperty("grant_types_supported").EnumerateArray().Select(item => item.GetString()));
    }

    private sealed record World(
        string Email, string Password,
        string OrdersApplicationCode, string OrdersApi, string OrdersScope, string OrdersPermission,
        string BillingApi, string BillingScope,
        string WebClient, string ServiceClient, string ServiceSecret,
        string OrdersApiClient, string OrdersApiSecret, string BillingApiClient, string BillingApiSecret);

    /// <summary>
    /// An Orders application with an Orders API (and permission), a Billing API in the same
    /// application, a web client of AuthCenter's application, a machine client and an API client
    /// per API, and a user who may use Orders.
    /// </summary>
    private async Task<World> CreateWorldAsync(bool grantOrdersAccess = true)
    {
        using var admin = await CreateAdminClientAsync();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var orders = await CreateApplicationAsync(admin);
        var ordersApi = $"https://orders-{suffix}.test/api";
        var billingApi = $"https://billing-{suffix}.test/api";
        var ordersScope = $"orders{suffix}.read";
        var billingScope = $"billing{suffix}.read";
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/api-resources", ApiRequest(orders.Id, ordersApi, ordersScope))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/api-resources", ApiRequest(orders.Id, billingApi, billingScope))).StatusCode);

        var authCenterId = await ApplicationIdAsync(DomainConstants.SystemCodes.AuthCenter);
        var webClient = await CreateClientAsync(admin, ClientRequest(authCenterId, ["openid", "offline_access", ordersScope, billingScope], public_: true));
        var (serviceClient, serviceSecret) = await CreateConfidentialClientAsync(admin, orders.Id, [ordersScope], ["client_credentials"]);
        var (ordersApiClient, ordersApiSecret) = await CreateConfidentialClientAsync(admin, orders.Id, [billingScope], [DomainConstants.OAuthGrantTypes.TokenExchange]);
        var (billingApiClient, billingApiSecret) = await CreateConfidentialClientAsync(admin, await ApplicationIdAsync(DomainConstants.SystemCodes.AuthCenter), [ordersScope], [DomainConstants.OAuthGrantTypes.TokenExchange]);

        var permission = $"ORDERS_{suffix.ToUpperInvariant()}_READ";
        var email = $"api-user-{suffix}@example.com";
        var password = TestSecretGenerator.CreatePassword();
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser { Id = Guid.NewGuid(), FullName = "API User", Email = email, UserName = email, EmailConfirmed = true, HasLocalPassword = true, IsActive = true, CreatedAt = DateTime.UtcNow };
            Assert.True((await users.CreateAsync(user, password)).Succeeded);
            db.UserApplicationAccesses.Add(new UserApplicationAccess { Id = Guid.NewGuid(), UserId = user.Id, ApplicationSystemId = authCenterId, IsActive = true, CreatedAt = DateTime.UtcNow });
            if (grantOrdersAccess)
                db.UserApplicationAccesses.Add(new UserApplicationAccess { Id = Guid.NewGuid(), UserId = user.Id, ApplicationSystemId = orders.Id, IsActive = true, CreatedAt = DateTime.UtcNow });
            var entity = new Permission { Id = Guid.NewGuid(), ApplicationSystemId = orders.Id, Code = permission, Name = "Read orders", IsActive = true, CreatedAt = DateTime.UtcNow };
            var role = new ApplicationRole { Id = Guid.NewGuid(), Name = $"ORDERS_READER_{suffix}", NormalizedName = $"ORDERS_READER_{suffix}".ToUpperInvariant(), DisplayName = "Orders reader", ApplicationSystemId = orders.Id, IsActive = true, CreatedAt = DateTime.UtcNow };
            db.Permissions.Add(entity);
            db.Roles.Add(role);
            db.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = entity.Id, CreatedAt = DateTime.UtcNow });
            db.UserRoles.Add(new IdentityUserRole<Guid> { UserId = user.Id, RoleId = role.Id });
            await db.SaveChangesAsync();
        }

        return new World(email, password, orders.Code, ordersApi, ordersScope, permission, billingApi, billingScope,
            webClient, serviceClient, serviceSecret, ordersApiClient, ordersApiSecret, billingApiClient, billingApiSecret);
    }

    private static object ApiRequest(Guid applicationId, string identifier, string scope) => new
    {
        ApplicationSystemId = applicationId,
        Identifier = identifier,
        DisplayName = "Test API",
        Scopes = new[] { new { Name = scope, DisplayName = scope } }
    };

    private static object ClientRequest(Guid applicationId, string[] scopes, bool public_) => new
    {
        ApplicationSystemId = applicationId,
        ClientId = $"api-web-{Guid.NewGuid():N}"[..24],
        DisplayName = "API test web client",
        ClientType = public_ ? 1 : 0,
        RedirectUris = new[] { Redirect },
        AllowedScopes = scopes,
        GrantTypes = scopes.Contains("offline_access") ? new[] { "authorization_code", "refresh_token" } : new[] { "authorization_code" },
        LoginUrl = $"{HttpsAuthCenterFactory.Authority}/login",
        RequirePkce = true,
        AutoConsent = true
    };

    private static async Task<string> CreateClientAsync(HttpClient admin, object request)
    {
        var response = await admin.PostAsJsonAsync("/api/oauth/clients", request);
        return (await ReadDataAsync(response)).GetProperty("client").GetProperty("clientId").GetString()!;
    }

    private static async Task<(string ClientId, string Secret)> CreateConfidentialClientAsync(HttpClient admin, Guid applicationId, string[] scopes, string[] grants)
    {
        var response = await admin.PostAsJsonAsync("/api/oauth/clients", new
        {
            ApplicationSystemId = applicationId,
            ClientId = $"api-svc-{Guid.NewGuid():N}"[..24],
            DisplayName = "API test service",
            ClientType = 0,
            RedirectUris = Array.Empty<string>(),
            AllowedScopes = scopes,
            GrantTypes = grants,
            LoginUrl = $"{HttpsAuthCenterFactory.Authority}/login",
            RequirePkce = false
        });
        var data = await ReadDataAsync(response);
        return (data.GetProperty("client").GetProperty("clientId").GetString()!, data.GetProperty("clientSecret").GetString()!);
    }

    private static Dictionary<string, string> ExchangeRequest(string clientId, string secret, string subjectToken, string resource) => new()
    {
        ["grant_type"] = DomainConstants.OAuthGrantTypes.TokenExchange,
        ["client_id"] = clientId,
        ["client_secret"] = secret,
        ["subject_token"] = subjectToken,
        ["subject_token_type"] = DomainConstants.OAuthTokenTypes.AccessToken,
        ["resource"] = resource
    };

    private async Task<JsonElement> IntrospectAsync(string clientId, string secret, string token)
    {
        using var client = _factory.CreateAuthCenterClient();
        var response = await client.PostAsync("/oauth/introspect", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = secret,
            ["token"] = token
        }));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private HttpClient CreateBrowser() => _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri(HttpsAuthCenterFactory.Authority),
        AllowAutoRedirect = false,
        HandleCookies = true
    });

    private static async Task<string> SignInAsync(HttpClient browser, string email, string password)
    {
        var login = await browser.PostAsJsonAsync("/ui-api/session/login", new LoginRequest { Email = email, Password = password, ApplicationCode = DomainConstants.SystemCodes.AuthCenter });
        return (await ReadDataAsync(login)).GetProperty("csrfToken").GetString()!;
    }

    private async Task<JsonElement> AuthorizeAndExchangeAsync(HttpClient browser, string clientId, string scope)
    {
        var (url, verifier) = AuthorizeUrl(clientId, scope);
        var authorize = await browser.GetAsync(url);
        var code = QueryHelpers.ParseQuery(authorize.Headers.Location!.Query)["code"].ToString();
        Assert.False(string.IsNullOrEmpty(code), authorize.Headers.Location.OriginalString);
        return await TokenJsonAsync(new() { ["grant_type"] = "authorization_code", ["client_id"] = clientId, ["code"] = code, ["redirect_uri"] = Redirect, ["code_verifier"] = verifier });
    }

    private static async Task<string?> AuthorizeErrorAsync(HttpClient browser, string clientId, string scope, string[] resources)
    {
        var (url, _) = AuthorizeUrl(clientId, scope);
        foreach (var resource in resources)
            url += $"&resource={Uri.EscapeDataString(resource)}";
        var response = await browser.GetAsync(url);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var query = QueryHelpers.ParseQuery(response.Headers.Location!.Query);
        return query.TryGetValue("error", out var error) ? error.ToString() : null;
    }

    private static (string Url, string Verifier) AuthorizeUrl(string clientId, string scope)
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        return (QueryHelpers.AddQueryString("/oauth/authorize", new Dictionary<string, string?>
        {
            ["response_type"] = "code",
            ["client_id"] = clientId,
            ["redirect_uri"] = Redirect,
            ["scope"] = scope,
            ["state"] = "state-value",
            ["nonce"] = "nonce-value",
            ["code_challenge"] = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))),
            ["code_challenge_method"] = "S256"
        }), verifier);
    }

    private async Task<HttpResponseMessage> TokenAsync(Dictionary<string, string> form)
    {
        using var client = _factory.CreateAuthCenterClient();
        return await client.PostAsync("/oauth/token", new FormUrlEncodedContent(form));
    }

    private async Task<JsonElement> TokenJsonAsync(Dictionary<string, string> form)
    {
        var response = await TokenAsync(form);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<string?> OAuthErrorAsync(HttpResponseMessage response)
    {
        Assert.False(response.IsSuccessStatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString();
    }

    private static async Task<(Guid Id, string Code)> CreateApplicationAsync(HttpClient admin)
    {
        var code = "APIRES" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var created = await ReadDataAsync(await admin.PostAsJsonAsync("/api/applications", new { Code = code, Name = "API resource app " + code, RegistrationMode = "Closed" }));
        return (created.GetProperty("id").GetGuid(), code);
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

    private async Task<Guid> ApplicationIdAsync(string code)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
        return await db.ApplicationSystems.Where(application => application.Code == code).Select(application => application.Id).SingleAsync();
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ApiResponse<object>>())?.ErrorCode;

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
