using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using AuthCenter.Client;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace AuthCenter.UnitTests;

public sealed class AuthCenterClientSdkTests
{
    [Fact]
    public void BffOptions_RejectInsecureOrPublicClientConfiguration()
    {
        Assert.Throws<ArgumentException>(() => new AuthCenterBffOptions
        {
            Authority = new Uri("http://identity.example.test"),
            ClientId = "web",
            ClientSecret = "secret"
        }.Validate());
        Assert.Throws<ArgumentException>(() => new AuthCenterBffOptions
        {
            Authority = new Uri("https://identity.example.test"),
            ClientId = "web",
            ClientSecret = "",
        }.Validate());
        Assert.Throws<ArgumentException>(() => new AuthCenterBffOptions
        {
            Authority = new Uri("https://identity.example.test"),
            ClientId = "web",
            ClientSecret = "secret",
            Scopes = ["profile"]
        }.Validate());
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/orders/42?tab=history")]
    public void BffReturnUrl_AcceptsOnlyLocalPaths(string value) =>
        Assert.True(AuthCenterBffEndpointRouteBuilderExtensions.IsLocalReturnUrl(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://attacker.example")]
    [InlineData("//attacker.example")]
    [InlineData("/\\attacker.example")]
    public void BffReturnUrl_RejectsExternalOrAmbiguousValues(string? value) =>
        Assert.False(AuthCenterBffEndpointRouteBuilderExtensions.IsLocalReturnUrl(value));

    [Fact]
    public void AuthorizationUri_CarriesFederationHints_AndRejectsMalformedOnes()
    {
        var client = new AuthCenterClient(new HttpClient(), new AuthCenterClientOptions { Authority = new Uri("https://identity.example.test"), ClientId = "web" });
        var provider = Guid.NewGuid();
        var uri = client.BuildAuthorizationUri(new Uri("https://app.example.test/callback"), "state", "nonce", AuthCenterClient.CreatePkce(),
            identityProvider: provider.ToString("N"), domainHint: "@Contoso.COM");
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);
        Assert.Equal(provider.ToString(), query["idp"].ToString());
        Assert.Equal("contoso.com", query["domain_hint"].ToString());

        Assert.Throws<ArgumentException>(() => client.BuildAuthorizationUri(new Uri("https://app.example.test/callback"), "state", "nonce", AuthCenterClient.CreatePkce(), identityProvider: "corporate"));
        Assert.Throws<ArgumentException>(() => client.BuildAuthorizationUri(new Uri("https://app.example.test/callback"), "state", "nonce", AuthCenterClient.CreatePkce(), domainHint: "not a domain"));
    }

    [Fact]
    public void AccessTokenClaims_AreBoundToValidatedOidcSubjectAndClient()
    {
        var options = ValidOptions();
        var identity = new ClaimsIdentity([new Claim("sub", "user-1")], "oidc", "name", ClaimTypes.Role);
        var principal = new ClaimsPrincipal(identity);
        var accessTokenPrincipal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("sub", "user-1"),
                new Claim("permissions", "ORDERS_READ"),
                new Claim("applications", "SHOP"),
                new Claim("role", "Operator"),
                new Claim("scope", "openid orders.read")
            ], "jwt"));

        AuthCenterAccessTokenPrincipalFactory.Enrich(
            principal,
            accessTokenPrincipal);

        Assert.True(principal.HasAuthCenterPermission("ORDERS_READ"));
        Assert.True(principal.HasAuthCenterScope("orders.read"));
        Assert.True(principal.IsInRole("Operator"));
        Assert.Contains(principal.FindAll("applications"), claim => claim.Value == "SHOP");
    }

    [Fact]
    public void AccessTokenClaims_RejectSubjectMismatch()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "user-1")], "oidc"));
        var accessTokenPrincipal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "user-2")], "jwt"));

        Assert.Throws<InvalidOperationException>(() => AuthCenterAccessTokenPrincipalFactory.Enrich(
            principal,
            accessTokenPrincipal));
    }

    [Fact]
    public async Task JwtBearerRegistration_UsesStrictAudienceAlgorithmAndRoleClaim()
    {
        var services = new ServiceCollection();
        services.AddAuthentication().AddAuthCenterJwtBearer(
            new Uri("https://identity.example.test"),
            "orders-api");
        await using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);

        Assert.Equal(["orders-api"], options.TokenValidationParameters.ValidAudiences);
        Assert.Equal([SecurityAlgorithms.RsaSha256], options.TokenValidationParameters.ValidAlgorithms);
        Assert.Equal(AuthCenterBffDefaults.RoleClaim, options.TokenValidationParameters.RoleClaimType);
        Assert.Equal(["at+jwt"], options.TokenValidationParameters.ValidTypes);
        Assert.True(options.TokenValidationParameters.ValidateIssuerSigningKey);
    }

    [Fact]
    public async Task AccessTokenValidator_VerifiesSignatureIssuerAudienceAndLifetime()
    {
        using var rsa = RSA.Create(2048);
        var signingKey = new RsaSecurityKey(rsa) { KeyId = "test-key" };
        var issuer = "https://identity.example.test";
        var configuration = new OpenIdConnectConfiguration { Issuer = issuer };
        configuration.SigningKeys.Add(signingKey);
        var oidcOptions = new OpenIdConnectOptions
        {
            ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration),
            TokenValidationParameters = new TokenValidationParameters
            {
                ClockSkew = TimeSpan.Zero,
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256]
            }
        };
        var validator = new AuthCenterAccessTokenValidator(
            new StaticOptionsMonitor<OpenIdConnectOptions>(oidcOptions),
            ValidOptions());
        var principal = await validator.ValidateAsync(
            CreateToken(signingKey, issuer, "shop-web", "at+jwt"),
            CancellationToken.None);

        Assert.Equal("user-1", principal.FindFirstValue("sub"));

        await Assert.ThrowsAsync<SecurityTokenException>(() => validator.ValidateAsync(
            CreateToken(signingKey, issuer, "another-client", "at+jwt"),
            CancellationToken.None));

        // An ID token shares issuer, audience and algorithm; its "JWT" type must not pass as an access token.
        await Assert.ThrowsAsync<SecurityTokenException>(() => validator.ValidateAsync(
            CreateToken(signingKey, issuer, "shop-web", "JWT"),
            CancellationToken.None));
    }

    [Fact]
    public void AccessTokenClaims_MapLegacyAndShortRoleClaimsToTheIdentityRoleType()
    {
        foreach (var roleClaimType in new[] { "role", ClaimTypes.Role })
        {
            var identity = new ClaimsIdentity([new Claim("sub", "user-1")], "oidc", "name", "role");
            var principal = new ClaimsPrincipal(identity);
            var accessTokenPrincipal = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("sub", "user-1"), new Claim(roleClaimType, "Operator")], "jwt"));

            AuthCenterAccessTokenPrincipalFactory.Enrich(principal, accessTokenPrincipal);

            Assert.True(principal.IsInRole("Operator"), roleClaimType);
        }
    }

    private static string CreateToken(SecurityKey key, string issuer, string audience, string type)
    {
        var header = new JwtHeader(new SigningCredentials(key, SecurityAlgorithms.RsaSha256), null, type);
        var payload = new JwtPayload(issuer, audience, [new Claim("sub", "user-1")], null, DateTime.UtcNow.AddMinutes(5));
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(header, payload));
    }

    [Fact]
    public async Task DistributedTicketStore_ProtectsAndRestoresAuthenticationTicket()
    {
        var services = new ServiceCollection();
        services.AddDistributedMemoryCache();
        await using var provider = services.BuildServiceProvider();
        var cache = provider.GetRequiredService<IDistributedCache>();
        var store = new ProtectedDistributedTicketStore(cache, new EphemeralDataProtectionProvider(), ValidOptions());
        var properties = new AuthenticationProperties { ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(10) };
        var original = new AuthenticationTicket(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "user-1")], "cookie")),
            properties,
            AuthCenterBffDefaults.CookieScheme);

        var key = await store.StoreAsync(original);
        var restored = await store.RetrieveAsync(key);

        Assert.NotNull(restored);
        Assert.Equal("user-1", restored.Principal.FindFirstValue("sub"));
        await store.RemoveAsync(key);
        Assert.Null(await store.RetrieveAsync(key));
    }

    [Fact]
    public async Task RefreshCoordinator_DeduplicatesConcurrentRotationAndSharesResult()
    {
        var coordinator = new InMemoryAuthCenterRefreshCoordinator();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invocationCount = 0;

        async Task<OAuthTokenSet> Rotate(CancellationToken _)
        {
            Interlocked.Increment(ref invocationCount);
            await release.Task;
            return new OAuthTokenSet
            {
                AccessToken = "new-access-token",
                RefreshToken = "new-refresh-token",
                ExpiresIn = 900
            };
        }

        var first = coordinator.CoordinateAsync("old-refresh-token", Rotate);
        var second = coordinator.CoordinateAsync("old-refresh-token", Rotate);
        Assert.Equal(1, Volatile.Read(ref invocationCount));

        release.SetResult();
        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, Volatile.Read(ref invocationCount));
        Assert.All(results, result => Assert.Equal("new-refresh-token", result.RefreshToken));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(new[] { "orders.read", "orders.read" }, "orders.read")]
    public async Task ClientCredentials_SendsOnlyExplicitMachineScopes(string[]? scopes, string? expectedScope)
    {
        var handler = new CapturingHandler();
        var client = new AuthCenterClient(new HttpClient(handler), new AuthCenterClientOptions
        {
            Authority = new Uri("https://identity.example.test"),
            ClientId = "orders-worker",
            ClientSecret = "test-only-secret-with-32-characters"
        });

        await client.ClientCredentialsAsync(scopes);

        var form = System.Web.HttpUtility.ParseQueryString(handler.Body!);
        Assert.Equal("client_credentials", form["grant_type"]);
        Assert.Equal(expectedScope, form["scope"]);
        Assert.Equal("Basic", handler.Authorization?.Scheme);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }
        public System.Net.Http.Headers.AuthenticationHeaderValue? Authorization { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Authorization = request.Headers.Authorization;
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"access_token\":\"token\",\"token_type\":\"Bearer\",\"expires_in\":900}", System.Text.Encoding.UTF8, "application/json")
            };
        }
    }

    private static AuthCenterBffOptions ValidOptions() => new()
    {
        Authority = new Uri("https://identity.example.test"),
        ClientId = "shop-web",
        ClientSecret = "test-only-secret-with-32-characters"
    };

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
