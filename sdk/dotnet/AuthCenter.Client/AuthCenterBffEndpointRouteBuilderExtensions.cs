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
        // login_hint and acr_values are forwarded to AuthCenter when they are well formed.
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
            await context.ChallengeAsync(AuthCenterBffDefaults.OpenIdConnectScheme, properties);
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
