using System.Text.Json;
using AuthCenter.Infrastructure.Persistence;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace AuthCenter.Api.Authorization;

/// <summary>
/// Chooses the CORS policy by endpoint instead of one global, credentialed policy:
/// discovery and keys are public; the token, revocation and UserInfo endpoints answer only the
/// browser origins registered on OAuth clients, never with credentials; the first-party API
/// answers the configured Cors:AllowedOrigins; the hosted UI and its cookie session get no CORS.
/// </summary>
public sealed class AuthCenterCorsPolicyProvider : ICorsPolicyProvider
{
    private static readonly TimeSpan ClientOriginsCacheLifetime = TimeSpan.FromSeconds(60);

    private static readonly CorsPolicy PublicMetadata = new CorsPolicyBuilder()
        .AllowAnyOrigin()
        .WithMethods("GET")
        .Build();

    private readonly CorsPolicy _firstParty;
    private readonly IServiceScopeFactory _scopes;
    private readonly IMemoryCache _cache;

    public AuthCenterCorsPolicyProvider(IConfiguration configuration, IWebHostEnvironment environment, IServiceScopeFactory scopes, IMemoryCache cache)
    {
        _scopes = scopes;
        _cache = cache;
        var origins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
        var builder = new CorsPolicyBuilder();
        if (origins.Length > 0)
            builder.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().AllowCredentials();
        else if (environment.IsDevelopment() || environment.IsEnvironment("Testing"))
            builder.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
        else
            builder.WithOrigins("http://localhost", "https://localhost").AllowAnyHeader().AllowAnyMethod();
        _firstParty = builder.Build();
    }

    public async Task<CorsPolicy?> GetPolicyAsync(HttpContext context, string? policyName)
    {
        var path = context.Request.Path;
        if (path.StartsWithSegments("/.well-known"))
            return PublicMetadata;
        if (path.StartsWithSegments("/oauth/token") || path.StartsWithSegments("/oauth/revoke") || path.StartsWithSegments("/oauth/userinfo"))
            return await ClientOriginsPolicyAsync();
        if (path.StartsWithSegments("/api"))
            return _firstParty;
        return null;
    }

    private async Task<CorsPolicy> ClientOriginsPolicyAsync()
    {
        var origins = await _cache.GetOrCreateAsync(Infrastructure.Services.OAuthClientService.CorsOriginsCacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = ClientOriginsCacheLifetime;
            await using var scope = _scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
            var registered = await db.OAuthClients.AsNoTracking()
                .Where(client => client.IsActive && client.AllowedCorsOriginsJson != "[]")
                .Select(client => client.AllowedCorsOriginsJson)
                .ToListAsync();
            return registered
                .SelectMany(json => JsonSerializer.Deserialize<string[]>(json) ?? [])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }) ?? [];

        // Browser clients authenticate with PKCE or a bearer token, never with cookies here.
        var builder = new CorsPolicyBuilder().WithMethods("GET", "POST").WithHeaders("Authorization", "Content-Type");
        return origins.Length == 0 ? builder.Build() : builder.WithOrigins(origins).Build();
    }
}
