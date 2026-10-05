using AuthCenter.Api.Authorization;
using AuthCenter.Api.Extensions;
using AuthCenter.Api.Middleware;
using AuthCenter.Application.Interfaces;
using AuthCenter.Application.Models;
using AuthCenter.Contracts.Responses;
using AuthCenter.Domain.Enums;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AuthCenter.Api.Controllers;

/// <summary>
/// AuthCenter as a SAML 2.0 identity provider: metadata, single sign-on (HTTP-Redirect and HTTP-POST
/// requests, HTTP-POST responses, and sign-ins started from AuthCenter) and single logout. A request
/// that needs the user continues in the hosted login, like an OpenID Connect authorization.
/// </summary>
[ApiController]
[Route("saml/idp")]
public sealed class SamlIdpController : ControllerBase
{
    private readonly ISamlIdentityProviderService _saml;

    private const string SignInErrorTitle = "No se pudo iniciar sesión en la aplicación";
    private const string LogoutErrorTitle = "No se pudo cerrar la sesión";

    public SamlIdpController(ISamlIdentityProviderService saml) => _saml = saml;

    [AllowAnonymous]
    [HttpGet("metadata")]
    public IActionResult Metadata()
    {
        var metadata = _saml.Metadata();
        return metadata is null
            ? ErrorPage(StatusCodes.Status503ServiceUnavailable, "El inicio de sesión SAML no está configurado.")
            : Content(metadata, "application/samlmetadata+xml");
    }

    [AllowAnonymous]
    [HttpGet("sso")]
    [EnableRateLimiting(RateLimitingExtensions.SamlIdentityProvider)]
    public async Task<IActionResult> SingleSignOn(CancellationToken ct) =>
        await SignInAsync(new SamlSignInStart(SamlRequestBinding.Redirect, Request.Query["SAMLRequest"], NullIfEmpty(Request.Query["RelayState"]), Request.QueryString.Value), ct);

    /// <summary>
    /// The HTTP-POST binding. A cross-site POST carries no Lax session cookie, so the request is kept
    /// for the top-level GET that follows, which does.
    /// </summary>
    [AllowAnonymous]
    [HttpPost("sso")]
    [IgnoreUiCsrf]
    [Consumes("application/x-www-form-urlencoded")]
    [EnableRateLimiting(RateLimitingExtensions.SamlIdentityProvider)]
    public async Task<IActionResult> SingleSignOnPost(CancellationToken ct)
    {
        var form = await Request.ReadFormAsync(ct);
        if (string.IsNullOrEmpty(form["SAMLRequest"]))
            return ErrorPage(StatusCodes.Status400BadRequest, "No pudimos leer la solicitud de la aplicación.");
        var key = await _saml.KeepPostedMessageAsync(form["SAMLRequest"]!, NullIfEmpty(form["RelayState"]), ct);
        return SeeOther($"/saml/idp/sso/posted/{Uri.EscapeDataString(key)}");
    }

    [AllowAnonymous]
    [HttpGet("sso/posted/{key}")]
    [EnableRateLimiting(RateLimitingExtensions.SamlIdentityProvider)]
    public async Task<IActionResult> SingleSignOnPosted(string key, CancellationToken ct)
    {
        var posted = await _saml.TakePostedMessageAsync(key, ct);
        return posted is not { } message
            ? ErrorPage(StatusCodes.Status400BadRequest, "La solicitud de inicio de sesión expiró. Vuelve a la aplicación e inicia sesión de nuevo.")
            : await SignInAsync(new SamlSignInStart(SamlRequestBinding.Post, message.SamlMessage, message.RelayState, null), ct);
    }

    /// <summary>A sign-in started from AuthCenter (the portal) for an application that allows it.</summary>
    [AllowAnonymous]
    [HttpGet("sso/initiate/{serviceProviderId:guid}")]
    [EnableRateLimiting(RateLimitingExtensions.SamlIdentityProvider)]
    public async Task<IActionResult> Initiate(Guid serviceProviderId, CancellationToken ct) =>
        await SignInAsync(new SamlSignInStart(SamlRequestBinding.IdpInitiated, null, NullIfEmpty(Request.Query["RelayState"]), null, serviceProviderId), ct);

    [AllowAnonymous]
    [HttpGet("interactions/{interactionId}/context")]
    public async Task<IActionResult> InteractionContext(string interactionId, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await _saml.GetInteractionContextAsync(interactionId, BrowserBinding.Read(HttpContext), ct);
        return result.IsSuccess ? Ok(ApiResponse<object>.Ok(result.Data!)) : NotFound(ApiResponse<object>.Fail(result.ErrorCode, result.Message));
    }

    /// <summary>The hosted login finished signing in: the address that posts the response to the application.</summary>
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.UiCookie)]
    [HttpPost("interactions/{interactionId}/complete")]
    public async Task<IActionResult> CompleteInteraction(string interactionId, CancellationToken ct)
    {
        var result = await _saml.CompleteInteractionAsync(interactionId, HostedLoginCaller(), ct);
        return result.IsSuccess
            ? Ok(ApiResponse<object>.Ok(new { redirectUrl = $"/saml/idp/response/{Uri.EscapeDataString(result.Data!)}" }))
            : BadRequest(ApiResponse<object>.Fail(result.ErrorCode, result.Message));
    }

    /// <summary>The step-up the application requires, exactly as for OpenID Connect interactions.</summary>
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.UiCookie)]
    [EnableRateLimiting(RateLimitingExtensions.Login)]
    [HttpPost("interactions/{interactionId}/step-up")]
    public async Task<IActionResult> BeginStepUp(string interactionId, [FromServices] IAuthService auth, [FromServices] IMfaService mfa, CancellationToken ct)
    {
        var caller = HostedLoginCaller();
        if (caller.UserId is null)
            return Unauthorized();
        var requirement = await _saml.GetStepUpRequirementAsync(interactionId, caller, ct);
        if (!requirement.IsSuccess)
            return BadRequest(ApiResponse<object>.Fail(requirement.ErrorCode, requirement.Message));
        if (requirement.Data!.RequiredAssurance is not { } required)
            return Ok(ApiResponse<object>.Ok(new { stepUpRequired = false }));
        var result = await auth.BeginStepUpAsync(caller.UserId.Value, requirement.Data.ApplicationCode, required, requirement.Data.PrimaryMethod, caller.IpAddress, caller.UserAgent, ct);
        return result.ErrorCode switch
        {
            "MFA_REQUIRED" => Ok(ApiResponse<object>.Ok(new { stepUpRequired = true, requiresMfa = true, mfaPendingToken = result.Message, mfaMethod = (await mfa.GetStatusAsync(caller.UserId.Value, ct)).Method == nameof(MfaMethod.EmailOtp) ? "email" : "totp" })),
            "MFA_SETUP_REQUIRED" => Ok(ApiResponse<object>.Ok(new { stepUpRequired = true, requiresMfaEnrollment = true, enrollmentToken = result.Message })),
            "PASSKEY_ENROLLMENT_REQUIRED" => Ok(ApiResponse<object>.Ok(new { stepUpRequired = true, requiresPasskeyEnrollment = true, enrollmentToken = result.Message })),
            _ => BadRequest(ApiResponse<object>.Fail(result.ErrorCode, result.Message))
        };
    }

    [AllowAnonymous]
    [HttpGet("response/{responseId}")]
    public async Task<IActionResult> PendingResponse(string responseId, CancellationToken ct)
    {
        var message = await _saml.TakePendingResponseAsync(responseId, BrowserBinding.Read(HttpContext), ct);
        return message is null
            ? ErrorPage(StatusCodes.Status404NotFound, "La respuesta para la aplicación expiró o ya se envió. Vuelve a la aplicación e inicia sesión de nuevo.")
            : Post(message);
    }

    [AllowAnonymous]
    [HttpGet("slo")]
    [EnableRateLimiting(RateLimitingExtensions.SamlIdentityProvider)]
    public async Task<IActionResult> SingleLogout(CancellationToken ct) =>
        await LogoutAsync(new SamlSignInStart(SamlRequestBinding.Redirect, Request.Query["SAMLRequest"], NullIfEmpty(Request.Query["RelayState"]), Request.QueryString.Value), ct);

    [AllowAnonymous]
    [HttpPost("slo")]
    [IgnoreUiCsrf]
    [Consumes("application/x-www-form-urlencoded")]
    [EnableRateLimiting(RateLimitingExtensions.SamlIdentityProvider)]
    public async Task<IActionResult> SingleLogoutPost(CancellationToken ct)
    {
        var form = await Request.ReadFormAsync(ct);
        if (string.IsNullOrEmpty(form["SAMLRequest"]))
            return ErrorPage(StatusCodes.Status400BadRequest, "No pudimos leer la solicitud de cierre de sesión de la aplicación.", LogoutErrorTitle);
        var key = await _saml.KeepPostedMessageAsync(form["SAMLRequest"]!, NullIfEmpty(form["RelayState"]), ct);
        return SeeOther($"/saml/idp/slo/posted/{Uri.EscapeDataString(key)}");
    }

    [AllowAnonymous]
    [HttpGet("slo/posted/{key}")]
    [EnableRateLimiting(RateLimitingExtensions.SamlIdentityProvider)]
    public async Task<IActionResult> SingleLogoutPosted(string key, CancellationToken ct)
    {
        var posted = await _saml.TakePostedMessageAsync(key, ct);
        return posted is not { } message
            ? ErrorPage(StatusCodes.Status400BadRequest, "La solicitud de cierre de sesión expiró. Vuelve a la aplicación e inténtalo de nuevo.", LogoutErrorTitle)
            : await LogoutAsync(new SamlSignInStart(SamlRequestBinding.Post, message.SamlMessage, message.RelayState, null), ct);
    }

    private async Task<IActionResult> SignInAsync(SamlSignInStart start, CancellationToken ct)
    {
        // The hosted-login cookie is SameSite=Lax: it reaches these top-level navigations, so an
        // existing single sign-on session answers without showing a page.
        var outcome = await _saml.SignInAsync(start, await BrowserCallerAsync(), ct);
        return Render(outcome);
    }

    private async Task<IActionResult> LogoutAsync(SamlSignInStart start, CancellationToken ct)
    {
        var outcome = await _saml.LogoutAsync(start, await BrowserCallerAsync(), ct);
        if (outcome.EndedBrowserSession)
        {
            await HttpContext.SignOutAsync(AuthenticationSchemes.UiCookie);
            Response.Cookies.Delete(UiCsrfMiddleware.CookieName, new CookieOptions { Secure = true, SameSite = SameSiteMode.Strict, Path = "/" });
        }
        return Render(outcome, LogoutErrorTitle);
    }

    private IActionResult Render(SamlEndpointOutcome outcome, string title = SignInErrorTitle) =>
        outcome.Post is { } message ? Post(message)
        : outcome.RedirectUrl is { } url ? Redirect(url)
        : ErrorPage(outcome.ErrorStatus, outcome.ErrorMessage ?? "No pudimos procesar la solicitud de la aplicación.", title);

    private static IActionResult Post(SamlPostMessage message) =>
        AuthorizationResponseResult.Create(new AuthorizationResponse
        {
            RedirectUri = message.Destination,
            ResponseMode = AuthorizationResponse.FormPost,
            Parameters = message.Fields
        });

    private async Task<AuthorizationCaller> BrowserCallerAsync()
    {
        var sso = await HttpContext.AuthenticateAsync(AuthenticationSchemes.UiCookie);
        return new AuthorizationCaller
        {
            UserId = sso.Succeeded ? SessionClaims.UserId(sso.Principal!) : null,
            SessionId = sso.Succeeded ? SessionClaims.SessionId(sso.Principal!) : null,
            BrowserBinding = BrowserBinding.Ensure(HttpContext),
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            UserAgent = Request.Headers.UserAgent.ToString()
        };
    }

    private AuthorizationCaller HostedLoginCaller() => new()
    {
        UserId = SessionClaims.UserId(User),
        SessionId = SessionClaims.SessionId(User),
        BrowserBinding = BrowserBinding.Read(HttpContext),
        RequiresBrowserBinding = true,
        IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
        UserAgent = Request.Headers.UserAgent.ToString()
    };

    /// <summary>
    /// A plain page in the hosted pages' style: an untrusted request is never answered at the
    /// address it names. It names no product, since end users only know the application.
    /// </summary>
    private ContentResult ErrorPage(int status, string message, string title = SignInErrorTitle)
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
        var html = "<!doctype html><html lang=\"es\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">" +
            "<title>" + title + "</title><link rel=\"stylesheet\" href=\"/assets/app.css\"></head><body><main class=\"auth-shell\"><section class=\"auth-panel\">" +
            "<h1>" + title + "</h1><p class=\"status error\" role=\"alert\">" + System.Net.WebUtility.HtmlEncode(message) + "</p>" +
            "<p><a href=\"/portal\">Ir a mis aplicaciones</a></p></section></main></body></html>";
        return new ContentResult { StatusCode = status, ContentType = "text/html; charset=utf-8", Content = html };
    }

    /// <summary>303: the browser follows the POST with a GET.</summary>
    private IActionResult SeeOther(string location)
    {
        Response.Headers.Location = location;
        return StatusCode(StatusCodes.Status303SeeOther);
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
