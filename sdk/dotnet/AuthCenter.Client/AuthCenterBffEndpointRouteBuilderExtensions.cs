using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace AuthCenter.Client;

public static class AuthCenterBffEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapAuthCenterBff(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var options = endpoints.ServiceProvider.GetRequiredService<AuthCenterBffOptions>();

        // The documented query parameter is return_url; returnUrl is accepted for callers that
        // relied on minimal-API name binding. Only local paths are honoured.
        Func<HttpContext, Task> loginHandler = async context =>
        {
            var returnUrl = context.Request.Query["return_url"].FirstOrDefault() ?? context.Request.Query["returnUrl"].FirstOrDefault();
            var destination = IsLocalReturnUrl(returnUrl) ? returnUrl! : "/";
            await context.ChallengeAsync(
                AuthCenterBffDefaults.OpenIdConnectScheme,
                new AuthenticationProperties { RedirectUri = destination });
        };
        endpoints.MapGet(options.LoginPath, loginHandler).AllowAnonymous();

        Func<HttpContext, IAntiforgery, IResult> sessionHandler = (context, antiforgery) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.Pragma = "no-cache";
            var csrf = antiforgery.GetAndStoreTokens(context);
            var user = context.User;
            return Results.Ok(new
            {
                authenticated = true,
                csrfToken = csrf.RequestToken,
                user = new
                {
                    subject = user.FindFirstValue("sub") ?? user.FindFirstValue(ClaimTypes.NameIdentifier),
                    name = user.FindFirstValue("name") ?? user.FindFirstValue(ClaimTypes.Name),
                    email = user.FindFirstValue("email") ?? user.FindFirstValue(ClaimTypes.Email),
                    roles = user.FindAll(ClaimTypes.Role).Select(item => item.Value).Distinct(StringComparer.Ordinal).ToArray(),
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

        Func<IResult> failureHandler = () => Results.Problem(
            title: "Authentication failed",
            detail: "The AuthCenter sign-in could not be completed. Start a new sign-in attempt.",
            statusCode: StatusCodes.Status401Unauthorized);
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
