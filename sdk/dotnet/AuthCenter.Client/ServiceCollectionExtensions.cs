using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;

namespace AuthCenter.Client;

public static class ServiceCollectionExtensions
{
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

    public static AuthenticationBuilder AddAuthCenterJwtBearer(
        this AuthenticationBuilder builder,
        Uri authority,
        IEnumerable<string> audiences,
        string scheme = JwtBearerDefaults.AuthenticationScheme)
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
                NameClaimType = "name",
                RoleClaimType = AuthCenterBffDefaults.RoleClaim
            };
        });
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
        services.AddSingleton<AuthCenterAccessTokenValidator>();
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

    private static bool IsSecureAuthority(Uri authority) =>
        authority.IsAbsoluteUri &&
        authority.Scheme == Uri.UriSchemeHttps &&
        string.IsNullOrEmpty(authority.UserInfo) &&
        string.IsNullOrEmpty(authority.Query) &&
        string.IsNullOrEmpty(authority.Fragment);
}
