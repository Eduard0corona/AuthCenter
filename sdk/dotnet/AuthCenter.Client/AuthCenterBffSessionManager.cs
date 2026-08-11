using System.Globalization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;

namespace AuthCenter.Client;

internal sealed class AuthCenterBffSessionManager(
    AuthCenterClient client,
    AuthCenterBffOptions options,
    IAuthCenterRefreshCoordinator refreshCoordinator,
    AuthCenterAccessTokenValidator accessTokenValidator) : IAuthCenterBffSessionManager
{
    public async Task<string?> GetAccessTokenAsync(HttpContext context, CancellationToken cancellationToken = default)
    {
        var authentication = await context.AuthenticateAsync(AuthCenterBffDefaults.CookieScheme);
        if (!authentication.Succeeded || authentication.Properties is null) return null;

        var expiration = ReadExpiration(authentication.Properties);
        if (expiration.HasValue && expiration.Value <= DateTimeOffset.UtcNow.Add(options.RefreshBeforeExpiration))
        {
            var refreshed = await RefreshAsync(context, cancellationToken);
            return refreshed.Succeeded ? refreshed.AccessToken : null;
        }

        return authentication.Properties?.GetTokenValue("access_token");
    }

    public async Task<AuthCenterBffRefreshResult> RefreshAsync(HttpContext context, CancellationToken cancellationToken = default)
    {
        var authentication = await context.AuthenticateAsync(AuthCenterBffDefaults.CookieScheme);
        if (!authentication.Succeeded || authentication.Principal is null || authentication.Properties is null)
            return new(false, "SESSION_NOT_AUTHENTICATED");

        var refreshToken = authentication.Properties.GetTokenValue("refresh_token");
        if (string.IsNullOrWhiteSpace(refreshToken)) return new(false, "REFRESH_TOKEN_UNAVAILABLE");

        OAuthTokenSet tokens;
        try
        {
            tokens = await refreshCoordinator.CoordinateAsync(
                refreshToken,
                refreshCancellationToken => client.RefreshAsync(refreshToken, refreshCancellationToken),
                cancellationToken);
        }
        catch (HttpRequestException)
        {
            await context.SignOutAsync(AuthCenterBffDefaults.CookieScheme);
            return new(false, "REFRESH_REJECTED");
        }

        var accessTokenPrincipal = await accessTokenValidator.ValidateAsync(tokens.AccessToken, cancellationToken);
        AuthCenterAccessTokenPrincipalFactory.Enrich(authentication.Principal, accessTokenPrincipal);
        var storedTokens = authentication.Properties.GetTokens()
            .Where(item => item.Name is not "access_token" and not "refresh_token" and not "expires_at")
            .ToList();
        storedTokens.Add(new AuthenticationToken { Name = "access_token", Value = tokens.AccessToken });
        storedTokens.Add(new AuthenticationToken { Name = "refresh_token", Value = tokens.RefreshToken ?? refreshToken });
        storedTokens.Add(new AuthenticationToken
        {
            Name = "expires_at",
            Value = DateTimeOffset.UtcNow.AddSeconds(tokens.ExpiresIn).ToString("O", CultureInfo.InvariantCulture)
        });
        authentication.Properties.StoreTokens(storedTokens);
        await context.SignInAsync(
            AuthCenterBffDefaults.CookieScheme,
            authentication.Principal,
            authentication.Properties);
        return new(true, AccessToken: tokens.AccessToken);
    }

    public async Task RevokeAndSignOutAsync(HttpContext context, CancellationToken cancellationToken = default)
    {
        var authentication = await context.AuthenticateAsync(AuthCenterBffDefaults.CookieScheme);
        var refreshToken = authentication.Properties?.GetTokenValue("refresh_token");
        try
        {
            if (!string.IsNullOrWhiteSpace(refreshToken))
                await client.RevokeAsync(refreshToken, cancellationToken);
        }
        finally
        {
            await context.SignOutAsync(AuthCenterBffDefaults.CookieScheme);
        }
    }

    private static DateTimeOffset? ReadExpiration(AuthenticationProperties properties)
    {
        var value = properties.GetTokenValue("expires_at");
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var expiration)
            ? expiration
            : null;
    }
}
