using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace AuthCenter.Client;

public static class AuthCenterBffEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapAuthCenterBff(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var options = endpoints.ServiceProvider.GetRequiredService<AuthCenterBffOptions>();

        // The documented query parameter is return_url; returnUrl is accepted for callers that
        // relied on minimal-API name binding. Only local paths are honoured. prompt, max_age,
        // login_hint, acr_values, idp and domain_hint are forwarded to AuthCenter when they are
        // well formed.
        Func<HttpContext, Task> loginHandler = async context =>
        {
            var query = context.Request.Query;
            var returnUrl = query["return_url"].FirstOrDefault() ?? query["returnUrl"].FirstOrDefault();
            var properties = new OpenIdConnectChallengeProperties { RedirectUri = IsLocalReturnUrl(returnUrl) ? returnUrl! : "/" };
            if (AuthCenterChallengeParameters.Prompt(query["prompt"].FirstOrDefault()) is { } prompt)
                properties.Prompt = prompt;
            if (AuthCenterChallengeParameters.MaxAge(query["max_age"].FirstOrDefault()) is { } maxAge)
                properties.MaxAge = maxAge;
            if (AuthCenterChallengeParameters.LoginHint(query["login_hint"].FirstOrDefault()) is { } loginHint)
                properties.SetParameter(OpenIdConnectParameterNames.LoginHint, loginHint);
            if (AuthCenterChallengeParameters.AcrValues(query["acr_values"].FirstOrDefault()) is { } acrValues)
                properties.SetParameter(OpenIdConnectParameterNames.AcrValues, acrValues);
            if (AuthCenterChallengeParameters.IdentityProvider(query[AuthCenterChallengeParameters.IdentityProviderParameter].FirstOrDefault()) is { } identityProvider)
                properties.SetParameter(AuthCenterChallengeParameters.IdentityProviderParameter, identityProvider);
            if (AuthCenterChallengeParameters.DomainHint(query[AuthCenterChallengeParameters.DomainHintParameter].FirstOrDefault()) is { } domainHint)
                properties.SetParameter(AuthCenterChallengeParameters.DomainHintParameter, domainHint);
            await context.ChallengeAsync(AuthCenterBffDefaults.OpenIdConnectScheme, properties);
        };
        endpoints.MapGet(options.LoginPath, loginHandler).AllowAnonymous();

        Func<HttpContext, IAntiforgery, IResult> sessionHandler = (context, antiforgery) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.Pragma = "no-cache";
            var csrf = antiforgery.GetAndStoreTokens(context);
            var user = context.User;
            var sessionId = user.FindFirstValue("sid");
            return Results.Ok(new
            {
                authenticated = true,
                csrfToken = csrf.RequestToken,
                // Navigating here signs out of AuthCenter too; the sid keeps other sites from doing it.
                logoutUrl = sessionId is null ? null : $"{options.LogoutPath}?sid={Uri.EscapeDataString(sessionId)}",
                user = new
                {
                    subject = user.FindFirstValue("sub") ?? user.FindFirstValue(ClaimTypes.NameIdentifier),
                    name = user.FindFirstValue("name") ?? user.FindFirstValue(ClaimTypes.Name),
                    email = user.FindFirstValue("email") ?? user.FindFirstValue(ClaimTypes.Email),
                    roles = user.Claims.Where(item => AuthCenterRoleClaims.IsRoleClaim(item.Type) ||
                            user.Identities.Any(identity => identity.RoleClaimType == item.Type))
                        .Select(item => item.Value).Distinct(StringComparer.Ordinal).ToArray(),
                    permissions = user.FindAll(AuthCenterBffDefaults.PermissionClaim).Select(item => item.Value).Distinct(StringComparer.Ordinal).ToArray(),
                    applications = user.FindAll(AuthCenterBffDefaults.ApplicationsClaim).Select(item => item.Value).Distinct(StringComparer.Ordinal).ToArray()
                }
            });
        };
        endpoints.MapGet(options.SessionPath, sessionHandler).RequireAuthorization(CookieAuthorization());

        Func<HttpContext, IAntiforgery, IAuthCenterBffSessionManager, CancellationToken, Task<IResult>> refreshHandler = async (
            HttpContext context,
            IAntiforgery antiforgery,
            IAuthCenterBffSessionManager sessions,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            await antiforgery.ValidateRequestAsync(context);
            var result = await sessions.RefreshAsync(context, cancellationToken);
            return result.Succeeded
                ? Results.NoContent()
                : Results.Json(new { errorCode = result.ErrorCode }, statusCode: StatusCodes.Status401Unauthorized);
        };
        endpoints.MapPost(options.RefreshPath, refreshHandler).RequireAuthorization(CookieAuthorization());

        Func<HttpContext, IAntiforgery, IAuthCenterBffSessionManager, CancellationToken, Task<IResult>> logoutHandler = async (
            HttpContext context,
            IAntiforgery antiforgery,
            IAuthCenterBffSessionManager sessions,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            await antiforgery.ValidateRequestAsync(context);
            await sessions.RevokeAndSignOutAsync(context, cancellationToken);
            return Results.NoContent();
        };
        endpoints.MapPost(options.LogoutPath, logoutHandler).RequireAuthorization(CookieAuthorization());

        // Global sign-out (OpenID Connect RP-Initiated Logout): revokes the refresh token, ends the
        // AuthCenter single sign-on session and returns through the signed-out callback. The sid
        // from /auth/session is required, so another site cannot sign the user out with a link.
        Func<HttpContext, AuthCenterClient, CancellationToken, Task> globalLogoutHandler = async (context, client, cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var sessionId = context.User.FindFirstValue("sid");
            var presented = context.Request.Query["sid"].FirstOrDefault();
            if (sessionId is null || presented is null || !string.Equals(sessionId, presented, StringComparison.Ordinal))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            var authentication = await context.AuthenticateAsync(AuthCenterBffDefaults.CookieScheme);
            var refreshToken = authentication.Properties?.GetTokenValue("refresh_token");
            if (!string.IsNullOrWhiteSpace(refreshToken))
            {
                try { await client.RevokeAsync(refreshToken, cancellationToken); }
                catch (HttpRequestException) { /* AuthCenter ends the session's grants on logout anyway. */ }
            }

            var returnUrl = context.Request.Query["return_url"].FirstOrDefault();
            // The OpenID Connect handler reads the ID token hint from the cookie session, so it signs
            // out first; the local session ends right after.
            await context.SignOutAsync(
                AuthCenterBffDefaults.OpenIdConnectScheme,
                new AuthenticationProperties { RedirectUri = IsLocalReturnUrl(returnUrl) ? returnUrl! : "/" });
            await context.SignOutAsync(AuthCenterBffDefaults.CookieScheme);
        };
        endpoints.MapGet(options.LogoutPath, globalLogoutHandler).RequireAuthorization(CookieAuthorization());

        Func<HttpContext, CancellationToken, Task<IResult>> backchannelLogoutHandler = async (context, cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!context.Request.HasFormContentType)
                return Results.Json(new { error = "invalid_request" }, statusCode: StatusCodes.Status400BadRequest);
            var form = await context.Request.ReadFormAsync(cancellationToken);
            var logout = context.RequestServices.GetRequiredService<AuthCenterBackchannelLogout>();
            return await logout.ProcessAsync(form["logout_token"].FirstOrDefault(), cancellationToken)
                ? Results.Ok()
                : Results.Json(new { error = "invalid_request" }, statusCode: StatusCodes.Status400BadRequest);
        };
        endpoints.MapPost(options.BackchannelLogoutPath, backchannelLogoutHandler).AllowAnonymous().DisableAntiforgery();

        // error carries a standard OpenID Connect code (for example login_required after a
        // prompt=none attempt) so the application can decide what to show next.
        Func<HttpContext, IResult> failureHandler = context =>
        {
            var error = AuthCenterChallengeParameters.ForwardedError(context.Request.Query["error"].FirstOrDefault());
            return Results.Problem(
                title: "Authentication failed",
                detail: "The AuthCenter sign-in could not be completed. Start a new sign-in attempt.",
                statusCode: StatusCodes.Status401Unauthorized,
                extensions: error is null ? null : new Dictionary<string, object?> { ["error"] = error });
        };
        endpoints.MapGet(options.RemoteFailurePath, failureHandler).AllowAnonymous();

        return endpoints;
    }

    internal static bool IsLocalReturnUrl(string? returnUrl) =>
        !string.IsNullOrWhiteSpace(returnUrl) &&
        returnUrl[0] == '/' &&
        (returnUrl.Length == 1 || (returnUrl[1] != '/' && returnUrl[1] != '\\'));

    private static AuthorizeAttribute CookieAuthorization() => new()
    {
        AuthenticationSchemes = AuthCenterBffDefaults.CookieScheme
    };
}
