using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;

namespace AuthCenter.Client;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Low-level client from configuration, for example the <c>AuthCenter</c> section:
    /// <c>Authority</c>, <c>ClientId</c>, optional <c>ClientSecret</c> and <c>Scopes</c>.
    /// </summary>
    public static IServiceCollection AddAuthCenterClient(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var defaults = new AuthCenterClientOptions { Authority = new Uri("https://authcenter.invalid"), ClientId = "defaults" };
        return services.AddAuthCenterClient(new AuthCenterClientOptions
        {
            Authority = RequiredUri(configuration, "Authority"),
            ClientId = Required(configuration, "ClientId"),
            ClientSecret = Optional(configuration, "ClientSecret"),
            Scopes = configuration.GetSection("Scopes").Get<string[]>() ?? defaults.Scopes
        });
    }

    public static IServiceCollection AddAuthCenterClient(this IServiceCollection services, AuthCenterClientOptions options)
    {
        if (!IsSecureAuthority(options.Authority))
            throw new ArgumentException("AuthCenter authority must be an absolute HTTPS URI.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.ClientId))
            throw new ArgumentException("AuthCenter client ID is required.", nameof(options));
        services.AddSingleton(options);
        services.AddHttpClient<AuthCenterClient>(client => client.Timeout = TimeSpan.FromSeconds(15));
        return services;
    }

    public static AuthenticationBuilder AddAuthCenterJwtBearer(this AuthenticationBuilder builder, Uri authority, string audience, string scheme = JwtBearerDefaults.AuthenticationScheme) =>
        AddAuthCenterJwtBearer(builder, authority, [audience], scheme);

    /// <summary>
    /// Resource-server validation from configuration: <c>Authority</c> and <c>Audience</c> (or an
    /// <c>Audiences</c> array), plus <c>RequireAccessTokenType</c> (default true).
    /// </summary>
    public static AuthenticationBuilder AddAuthCenterJwtBearer(this AuthenticationBuilder builder, IConfiguration configuration, string scheme = JwtBearerDefaults.AuthenticationScheme)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var audiences = configuration.GetSection("Audiences").Get<string[]>() ?? [];
        if (Optional(configuration, "Audience") is { } audience)
            audiences = [audience, .. audiences];
        if (audiences.Length == 0)
            throw new InvalidOperationException($"Set {Path(configuration, "Audience")} (or {Path(configuration, "Audiences")}) to the API identifier registered in AuthCenter.");
        return AddAuthCenterJwtBearer(builder, RequiredUri(configuration, "Authority"), audiences, scheme,
            configuration.GetValue("RequireAccessTokenType", true));
    }

    /// <summary>
    /// Validates AuthCenter access tokens: RS256 signature from discovery/JWKS, exact issuer, one of
    /// the accepted audiences, lifetime and the RFC 9068 <c>at+jwt</c> type, so an ID token cannot
    /// be replayed as a bearer token. Set <paramref name="requireAccessTokenType"/> to false only
    /// while an AuthCenter release older than the at+jwt contract is still issuing tokens.
    /// </summary>
    public static AuthenticationBuilder AddAuthCenterJwtBearer(
        this AuthenticationBuilder builder,
        Uri authority,
        IEnumerable<string> audiences,
        string scheme = JwtBearerDefaults.AuthenticationScheme,
        bool requireAccessTokenType = true)
    {
        if (!IsSecureAuthority(authority))
            throw new ArgumentException("AuthCenter authority must be an absolute HTTPS URI.", nameof(authority));
        var acceptedAudiences = audiences?.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).ToArray()
            ?? throw new ArgumentNullException(nameof(audiences));
        if (acceptedAudiences.Length == 0)
            throw new ArgumentException("At least one AuthCenter audience is required.", nameof(audiences));
        return builder.AddJwtBearer(scheme, options =>
        {
            options.Authority = authority.AbsoluteUri.TrimEnd('/');
            options.RequireHttpsMetadata = true;
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidAudiences = acceptedAudiences,
                ClockSkew = TimeSpan.FromSeconds(30),
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                ValidTypes = requireAccessTokenType ? [AuthCenterBffDefaults.AccessTokenType] : null,
                NameClaimType = "name",
                RoleClaimType = AuthCenterBffDefaults.RoleClaim
            };
            options.Events ??= new JwtBearerEvents();
            var validated = options.Events.OnTokenValidated;
            options.Events.OnTokenValidated = async context =>
            {
                if (context.Principal?.Identity is System.Security.Claims.ClaimsIdentity identity)
                    AuthCenterRoleClaims.NormalizeLegacyRoles(identity);
                await validated(context);
            };
        });
    }

    /// <summary>
    /// BFF from configuration, for example the <c>AuthCenter</c> section. <c>Authority</c>,
    /// <c>ClientId</c> and <c>ClientSecret</c> are required (keep the secret in a secret store);
    /// every other <see cref="AuthCenterBffOptions"/> property is optional and keeps its default.
    /// </summary>
    public static IServiceCollection AddAuthCenterBff(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var defaults = new AuthCenterBffOptions { Authority = new Uri("https://authcenter.invalid"), ClientId = "defaults", ClientSecret = "defaults" };
        return services.AddAuthCenterBff(new AuthCenterBffOptions
        {
            Authority = RequiredUri(configuration, "Authority"),
            ClientId = Required(configuration, "ClientId"),
            ClientSecret = Required(configuration, "ClientSecret"),
            Scopes = configuration.GetSection("Scopes").Get<string[]>() ?? defaults.Scopes,
            Resource = Optional(configuration, "Resource"),
            CookieName = Optional(configuration, "CookieName") ?? defaults.CookieName,
            CallbackPath = Optional(configuration, "CallbackPath") ?? defaults.CallbackPath,
            LoginPath = Optional(configuration, "LoginPath") ?? defaults.LoginPath,
            LogoutPath = Optional(configuration, "LogoutPath") ?? defaults.LogoutPath,
            SessionPath = Optional(configuration, "SessionPath") ?? defaults.SessionPath,
            RefreshPath = Optional(configuration, "RefreshPath") ?? defaults.RefreshPath,
            RemoteFailurePath = Optional(configuration, "RemoteFailurePath") ?? defaults.RemoteFailurePath,
            SignedOutCallbackPath = Optional(configuration, "SignedOutCallbackPath") ?? defaults.SignedOutCallbackPath,
            BackchannelLogoutPath = Optional(configuration, "BackchannelLogoutPath") ?? defaults.BackchannelLogoutPath,
            SessionLifetime = configuration.GetValue("SessionLifetime", defaults.SessionLifetime),
            RefreshBeforeExpiration = configuration.GetValue("RefreshBeforeExpiration", defaults.RefreshBeforeExpiration),
            UseDistributedRefreshCoordination = configuration.GetValue("UseDistributedRefreshCoordination", false)
        });
    }

    /// <summary>
    /// Replaces the per-instance refresh coordinator with <see cref="DistributedAuthCenterRefreshCoordinator"/>
    /// (the BFF runs on several instances with a shared distributed cache and Data Protection key ring).
    /// </summary>
    public static IServiceCollection AddAuthCenterDistributedRefreshCoordination(this IServiceCollection services, DistributedRefreshCoordinationOptions? options = null)
    {
        services.RemoveAll<IAuthCenterRefreshCoordinator>();
        services.AddSingleton<IAuthCenterRefreshCoordinator>(provider => new DistributedAuthCenterRefreshCoordinator(
            provider.GetRequiredService<IDistributedCache>(),
            provider.GetRequiredService<IDataProtectionProvider>(),
            options));
        return services;
    }

    public static IServiceCollection AddAuthCenterBff(
        this IServiceCollection services,
        AuthCenterBffOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        services.AddSingleton(options);
        services.AddAuthCenterClient(new AuthCenterClientOptions
        {
            Authority = options.Authority,
            ClientId = options.ClientId,
            ClientSecret = options.ClientSecret,
            Scopes = options.Scopes
        });
        services.AddDataProtection();
        services.AddDistributedMemoryCache();
        services.AddAntiforgery(antiforgery =>
        {
            antiforgery.HeaderName = AuthCenterBffDefaults.AntiforgeryHeaderName;
            antiforgery.Cookie.Name = "__Host-AuthCenter.Bff.Csrf";
            antiforgery.Cookie.HttpOnly = true;
            antiforgery.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            antiforgery.Cookie.SameSite = SameSiteMode.Strict;
            antiforgery.Cookie.Path = "/";
        });

        services.AddSingleton<ProtectedDistributedTicketStore>();
        services.AddSingleton<AuthCenterBackchannelLogout>();
        services.AddSingleton<AuthCenterAccessTokenValidator>();
        if (options.UseDistributedRefreshCoordination)
            services.AddAuthCenterDistributedRefreshCoordination();
        else
            services.TryAddSingleton<IAuthCenterRefreshCoordinator, InMemoryAuthCenterRefreshCoordinator>();
        services.AddScoped<IAuthCenterBffSessionManager, AuthCenterBffSessionManager>();
        services.AddScoped<AuthCenterOpenIdConnectEvents>();
        services.AddHttpContextAccessor();
        services.AddTransient<AuthCenterBffAccessTokenHandler>();

        services.AddAuthentication(authentication =>
        {
            authentication.DefaultScheme = AuthCenterBffDefaults.CookieScheme;
            authentication.DefaultAuthenticateScheme = AuthCenterBffDefaults.CookieScheme;
            authentication.DefaultSignInScheme = AuthCenterBffDefaults.CookieScheme;
            authentication.DefaultChallengeScheme = AuthCenterBffDefaults.OpenIdConnectScheme;
        })
        .AddCookie(AuthCenterBffDefaults.CookieScheme, cookie =>
        {
            cookie.Cookie.Name = options.CookieName;
            cookie.Cookie.HttpOnly = true;
            cookie.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            cookie.Cookie.SameSite = SameSiteMode.Lax;
            cookie.Cookie.Path = "/";
            cookie.Cookie.IsEssential = true;
            cookie.ExpireTimeSpan = options.SessionLifetime;
            cookie.SlidingExpiration = false;
            cookie.Events.OnRedirectToLogin = context => WriteStatusOrRedirect(context, StatusCodes.Status401Unauthorized);
            cookie.Events.OnRedirectToAccessDenied = context => WriteStatusOrRedirect(context, StatusCodes.Status403Forbidden);
            cookie.Events.OnValidatePrincipal = AuthCenterBackchannelLogout.ValidatePrincipalAsync;
        })
        .AddOpenIdConnect(AuthCenterBffDefaults.OpenIdConnectScheme, oidc =>
        {
            oidc.Authority = options.Authority.AbsoluteUri.TrimEnd('/');
            oidc.ClientId = options.ClientId;
            oidc.ClientSecret = options.ClientSecret;
            oidc.SignInScheme = AuthCenterBffDefaults.CookieScheme;
            oidc.ResponseType = "code";
            oidc.UsePkce = true;
            oidc.RequireHttpsMetadata = true;
            oidc.MapInboundClaims = false;
            oidc.SaveTokens = true;
            oidc.GetClaimsFromUserInfoEndpoint = true;
            oidc.CallbackPath = options.CallbackPath;
            oidc.SignedOutCallbackPath = options.SignedOutCallbackPath;
            oidc.SignedOutRedirectUri = "/";
            if (options.Resource is not null)
                oidc.Resource = options.Resource;
            oidc.EventsType = typeof(AuthCenterOpenIdConnectEvents);
            oidc.Scope.Clear();
            foreach (var scope in options.Scopes.Distinct(StringComparer.Ordinal)) oidc.Scope.Add(scope);
            oidc.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ClockSkew = TimeSpan.FromSeconds(30),
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                NameClaimType = "name",
                RoleClaimType = AuthCenterBffDefaults.RoleClaim
            };
        });

        services.AddOptions<CookieAuthenticationOptions>(AuthCenterBffDefaults.CookieScheme)
            .Configure<ProtectedDistributedTicketStore>((cookie, ticketStore) => cookie.SessionStore = ticketStore);
        services.AddAuthorization();
        return services;
    }

    private static Task WriteStatusOrRedirect(RedirectContext<CookieAuthenticationOptions> context, int statusCode)
    {
        if (context.Request.Path.StartsWithSegments("/api") ||
            context.Request.Path.StartsWithSegments("/auth") ||
            context.Request.Headers.Accept.Any(value => value?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true))
        {
            context.Response.StatusCode = statusCode;
            return Task.CompletedTask;
        }

        context.Response.Redirect(context.RedirectUri);
        return Task.CompletedTask;
    }

    private static string Required(IConfiguration configuration, string key) =>
        Optional(configuration, key) ?? throw new InvalidOperationException($"Set {Path(configuration, key)} in the application configuration.");

    private static string? Optional(IConfiguration configuration, string key) =>
        string.IsNullOrWhiteSpace(configuration[key]) ? null : configuration[key]!.Trim();

    private static Uri RequiredUri(IConfiguration configuration, string key) =>
        Uri.TryCreate(Required(configuration, key), UriKind.Absolute, out var uri)
            ? uri
            : throw new InvalidOperationException($"{Path(configuration, key)} must be an absolute URI.");

    private static string Path(IConfiguration configuration, string key) =>
        configuration is IConfigurationSection section ? $"{section.Path}:{key}" : key;

    private static bool IsSecureAuthority(Uri authority) =>
        authority.IsAbsoluteUri &&
        authority.Scheme == Uri.UriSchemeHttps &&
        string.IsNullOrEmpty(authority.UserInfo) &&
        string.IsNullOrEmpty(authority.Query) &&
        string.IsNullOrEmpty(authority.Fragment);
}
