using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace AuthCenter.Client;

/// <summary>
/// OpenID Connect Back-Channel Logout 1.0 for the BFF: validates logout tokens from AuthCenter and
/// records the ended AuthCenter session (sid) or user (sub) in the distributed cache, so every
/// application instance rejects the matching cookie sessions on their next request.
/// </summary>
internal sealed class AuthCenterBackchannelLogout
{
    private const string SessionPrefix = "authcenter:bff:logout:sid:";
    private const string SubjectPrefix = "authcenter:bff:logout:sub:";
    private const string ReplayPrefix = "authcenter:bff:logout:jti:";
    private static readonly TimeSpan MaximumTokenAge = TimeSpan.FromMinutes(5);

    private readonly IDistributedCache _cache;
    private readonly IOptionsMonitor<OpenIdConnectOptions> _oidcOptions;
    private readonly AuthCenterBffOptions _options;

    public AuthCenterBackchannelLogout(IDistributedCache cache, IOptionsMonitor<OpenIdConnectOptions> oidcOptions, AuthCenterBffOptions options)
    {
        _cache = cache;
        _oidcOptions = oidcOptions;
        _options = options;
    }

    /// <summary>Validates a logout token and ends the local sessions it names; false when it is not acceptable.</summary>
    public async Task<bool> ProcessAsync(string? logoutToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(logoutToken) || logoutToken.Length > 16_384)
            return false;

        var oidc = _oidcOptions.Get(AuthCenterBffDefaults.OpenIdConnectScheme);
        if (oidc.ConfigurationManager is null)
            return false;
        var configuration = await oidc.ConfigurationManager.GetConfigurationAsync(cancellationToken);

        ClaimsPrincipal principal;
        SecurityToken validatedToken;
        try
        {
            principal = new JwtSecurityTokenHandler { MapInboundClaims = false }.ValidateToken(logoutToken, new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = configuration.Issuer,
                ValidateAudience = true,
                ValidAudience = _options.ClientId,
                ValidateLifetime = true,
                RequireExpirationTime = true,
                ClockSkew = TimeSpan.FromSeconds(30),
                ValidateIssuerSigningKey = true,
                IssuerSigningKeys = configuration.SigningKeys,
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                // A logout token must never be confused with an ID or access token.
                ValidTypes = [AuthCenterBffDefaults.LogoutTokenType]
            }, out validatedToken);
        }
        catch (Exception exception) when (exception is SecurityTokenException or ArgumentException)
        {
            return false;
        }

        var sessionId = principal.FindFirst("sid")?.Value;
        var subject = principal.FindFirst("sub")?.Value;
        var tokenId = principal.FindFirst("jti")?.Value;
        if ((string.IsNullOrWhiteSpace(sessionId) && string.IsNullOrWhiteSpace(subject)) ||
            string.IsNullOrWhiteSpace(tokenId) ||
            principal.HasClaim(claim => claim.Type == "nonce") ||
            !HasLogoutEvent(principal.FindFirst("events")?.Value) ||
            !IsRecent(principal.FindFirst("iat")?.Value))
        {
            return false;
        }

        // Each logout token is accepted once.
        var replayKey = ReplayPrefix + tokenId;
        if (await _cache.GetAsync(replayKey, cancellationToken) is not null)
            return false;
        await _cache.SetAsync(replayKey, [1], new DistributedCacheEntryOptions
        {
            AbsoluteExpiration = validatedToken.ValidTo.AddMinutes(5)
        }, cancellationToken);

        var marker = new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = _options.SessionLifetime };
        if (!string.IsNullOrWhiteSpace(sessionId))
            await _cache.SetAsync(SessionPrefix + sessionId, [1], marker, cancellationToken);
        else
            await _cache.SetStringAsync(SubjectPrefix + subject, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), marker, cancellationToken);
        return true;
    }

    /// <summary>True when AuthCenter ended the session this cookie session was created from.</summary>
    public async Task<bool> IsEndedAsync(ClaimsPrincipal principal, AuthenticationProperties properties, CancellationToken cancellationToken)
    {
        var sessionId = principal.FindFirst("sid")?.Value;
        if (!string.IsNullOrWhiteSpace(sessionId) && await _cache.GetAsync(SessionPrefix + sessionId, cancellationToken) is not null)
            return true;

        var subject = principal.FindFirst("sub")?.Value ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrWhiteSpace(subject))
            return false;
        var endedAt = await _cache.GetStringAsync(SubjectPrefix + subject, cancellationToken);
        return long.TryParse(endedAt, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) &&
            properties.IssuedUtc is { } issued &&
            issued <= DateTimeOffset.FromUnixTimeSeconds(seconds);
    }

    /// <summary>Cookie validation hook: a session AuthCenter ended is signed out here too.</summary>
    public static async Task ValidatePrincipalAsync(CookieValidatePrincipalContext context)
    {
        if (context.Principal is null)
            return;
        var logout = context.HttpContext.RequestServices.GetRequiredService<AuthCenterBackchannelLogout>();
        if (await logout.IsEndedAsync(context.Principal, context.Properties, context.HttpContext.RequestAborted))
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(AuthCenterBffDefaults.CookieScheme);
        }
    }

    private static bool HasLogoutEvent(string? events)
    {
        if (string.IsNullOrWhiteSpace(events))
            return false;
        try
        {
            using var document = JsonDocument.Parse(events);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty(AuthCenterBffDefaults.BackchannelLogoutEvent, out var member) &&
                member.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsRecent(string? issuedAt) =>
        long.TryParse(issuedAt, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) &&
        DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(seconds) <= MaximumTokenAge;
}
