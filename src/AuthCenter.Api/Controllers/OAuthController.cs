using AuthCenter.Api.Authorization;
using AuthCenter.Api.Extensions;
using AuthCenter.Application.Common;
using AuthCenter.Application.Interfaces;
using AuthCenter.Application.Models;
using AuthCenter.Contracts.Requests.OAuth;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Responses.OAuth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using System.Text;

namespace AuthCenter.Api.Controllers;

[ApiController]
[Route("oauth")]
public class OAuthController : ControllerBase
{
    private readonly IOAuthAuthorizationService _oAuthService;
    private readonly ICurrentUserService _currentUserService;
    private readonly IAuthService _authService;
    private readonly IEndSessionService _endSession;

    public OAuthController(
        IOAuthAuthorizationService oAuthService,
        ICurrentUserService currentUserService,
        IAuthService authService,
        IEndSessionService endSession)
    {
        _oAuthService = oAuthService;
        _currentUserService = currentUserService;
        _authService = authService;
        _endSession = endSession;
    }

    [HttpGet("authorize")]
    public Task<IActionResult> Authorize(CancellationToken ct) => AuthorizeAsync(Request.Query, ct);

    // OpenID Connect Core section 3.1.2.1: the authorization endpoint also accepts form posts.
    [HttpPost("authorize")]
    [Consumes("application/x-www-form-urlencoded")]
    public async Task<IActionResult> AuthorizePost(CancellationToken ct) =>
        await AuthorizeAsync((await Request.ReadFormAsync(ct)).ToDictionary(item => item.Key, item => item.Value), ct);

    private async Task<IActionResult> AuthorizeAsync(IEnumerable<KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues>> parameters, CancellationToken ct)
    {
        var values = parameters.ToDictionary(item => item.Key, item => item.Value.ToString(), StringComparer.Ordinal);
        string? Value(string name) => values.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value) ? value : null;
        var request = new AuthorizeRequest
        {
            ResponseType = Value("response_type"),
            ClientId = Value("client_id"),
            RedirectUri = Value("redirect_uri"),
            Scope = Value("scope"),
            State = Value("state"),
            CodeChallenge = Value("code_challenge"),
            CodeChallengeMethod = Value("code_challenge_method"),
            Nonce = Value("nonce"),
            Prompt = Value("prompt"),
            MaxAge = Value("max_age"),
            LoginHint = Value("login_hint"),
            IdTokenHint = Value("id_token_hint"),
            AcrValues = Value("acr_values"),
            ResponseMode = Value("response_mode"),
            UiLocales = Value("ui_locales"),
            Request = Value("request"),
            RequestUri = Value("request_uri")
        };

        // The hosted-login cookie is SameSite=Lax, so it reaches this top-level navigation and an
        // existing single sign-on session can answer the client without showing a page.
        var sso = await HttpContext.AuthenticateAsync(AuthenticationSchemes.UiCookie);
        var caller = new AuthorizationCaller
        {
            UserId = sso.Succeeded ? SessionClaims.UserId(sso.Principal!) : null,
            SessionId = sso.Succeeded ? SessionClaims.SessionId(sso.Principal!) : null,
            BrowserBinding = BrowserBinding.Ensure(HttpContext),
            IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
            UserAgent = Request.Headers.UserAgent.ToString()
        };

        var result = await _oAuthService.InitiateAuthorizationAsync(request, caller, ct);
        if (!result.IsSuccess)
            return BadRequest(ApiResponse<object>.Fail(result.ErrorCode, result.Message));
        return result.Data!.Response is { } response ? AuthorizationResponseResult.Create(response) : Redirect(result.Data.LoginUrl!);
    }

    /// <summary>
    /// Non-sensitive context for the hosted login (application, login hint, freshness), readable
    /// before sign-in and only from the browser that started the authorization request.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("interactions/{interactionId}/context")]
    public async Task<IActionResult> GetInteractionContext(string interactionId, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await _oAuthService.GetInteractionContextAsync(interactionId, BrowserBinding.Read(HttpContext), ct);
        return result.IsSuccess
            ? Ok(ApiResponse<object>.Ok(result.Data!))
            : NotFound(ApiResponse<object>.Fail(result.ErrorCode, result.Message));
    }

    [Authorize]
    [HttpGet("interactions/{interactionId}")]
    public async Task<IActionResult> GetInteraction(string interactionId, CancellationToken ct)
    {
        var result = await _oAuthService.GetInteractionAsync(interactionId, CurrentCaller(), ct);
        if (!result.IsSuccess)
            return BadRequest(ApiResponse<object>.Fail(result.ErrorCode, result.Message));
        return Ok(ApiResponse<object>.Ok(result.Data!));
    }

    [Authorize]
    [HttpGet("consents")]
    public async Task<IActionResult> GetConsents(CancellationToken ct)
    {
        var userId = _currentUserService.UserId;
        if (userId is null) return Unauthorized();
        return Ok(ApiResponse<object>.Ok(await _oAuthService.GetConsentGrantsAsync(userId.Value, ct)));
    }

    [Authorize]
    [HttpDelete("consents/{grantId:guid}")]
    public async Task<IActionResult> RevokeConsent(Guid grantId, CancellationToken ct)
    {
        var userId = _currentUserService.UserId;
        if (userId is null) return Unauthorized();
        var result = await _oAuthService.RevokeConsentGrantAsync(userId.Value, grantId, ct);
        return result.IsSuccess
            ? Ok(ApiResponse.Ok("Consent and associated refresh sessions were revoked."))
            : NotFound(ApiResponse.Fail(result.ErrorCode, result.Message));
    }

    [Authorize]
    [HttpPost("authorize/complete")]
    public async Task<IActionResult> CompleteAuthorization([FromBody] CompleteAuthorizationRequest request, CancellationToken ct)
    {
        var caller = CurrentCaller();
        if (caller.UserId is null) return Unauthorized();

        var result = await _oAuthService.CompleteAuthorizationAsync(request, caller, ct);
        if (!result.IsSuccess)
            return BadRequest(ApiResponse<object>.Fail(result.ErrorCode, result.Message));

        var response = result.Data!;
        if (Request.Headers["X-AuthCenter-UI"] == "1")
        {
            // The hosted login navigates with location.assign; a form post response is rendered by
            // AuthCenter itself so its CSP can allow exactly the client's origin as form action.
            var redirectUrl = response.IsFormPost
                ? $"/oauth/authorize/response/{Uri.EscapeDataString(await _oAuthService.StorePendingResponseAsync(response, caller.BrowserBinding, ct))}"
                : response.ToRedirectUrl();
            return Ok(ApiResponse<object>.Ok(new { redirectUrl }));
        }

        return AuthorizationResponseResult.Create(response);
    }

    /// <summary>
    /// Starts the step-up the client's application requires for the hosted-login session: returns
    /// an MFA pending token for <c>/ui-api/session/mfa</c>, or why the level cannot be reached.
    /// </summary>
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.UiCookie)]
    [EnableRateLimiting(RateLimitingExtensions.Login)]
    [HttpPost("interactions/{interactionId}/step-up")]
    public async Task<IActionResult> BeginStepUp(string interactionId, CancellationToken ct)
    {
        var caller = CurrentCaller();
        if (caller.UserId is null) return Unauthorized();

        var requirement = await _oAuthService.GetStepUpRequirementAsync(interactionId, caller, ct);
        if (!requirement.IsSuccess)
            return BadRequest(ApiResponse<object>.Fail(requirement.ErrorCode, requirement.Message));
        if (requirement.Data!.RequiredAssurance is not { } required)
            return Ok(ApiResponse<object>.Ok(new { stepUpRequired = false }));

        var result = await _authService.BeginStepUpAsync(
            caller.UserId.Value, requirement.Data.ApplicationCode, required, requirement.Data.PrimaryMethod, caller.IpAddress, caller.UserAgent, ct);
        return result.ErrorCode == "MFA_REQUIRED"
            ? Ok(ApiResponse<object>.Ok(new { stepUpRequired = true, requiresMfa = true, mfaPendingToken = result.Message }))
            : BadRequest(ApiResponse<object>.Fail(result.ErrorCode, result.Message));
    }

    [AllowAnonymous]
    [HttpGet("authorize/response/{responseId}")]
    public async Task<IActionResult> AuthorizationResponsePage(string responseId, CancellationToken ct)
    {
        var response = await _oAuthService.TakePendingResponseAsync(responseId, BrowserBinding.Read(HttpContext), ct);
        return response is null
            ? NotFound(ApiResponse<object>.Fail("INVALID_RESPONSE", "The authorization response expired or was already delivered."))
            : AuthorizationResponseResult.Create(response);
    }

    /// <summary>
    /// OpenID Connect RP-Initiated Logout (end_session_endpoint). An ID token of the current
    /// session signs out at once; otherwise the hosted logout page asks the user to confirm.
    /// </summary>
    [HttpGet("logout")]
    public async Task<IActionResult> EndSession(CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        string? Value(string name) => Request.Query.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value) ? value.ToString() : null;
        var sso = await HttpContext.AuthenticateAsync(AuthenticationSchemes.UiCookie);
        var caller = new EndSessionCaller
        {
            UserId = sso.Succeeded ? SessionClaims.UserId(sso.Principal!) : null,
            SessionId = sso.Succeeded ? SessionClaims.SessionId(sso.Principal!) : null,
            BrowserBinding = BrowserBinding.Ensure(HttpContext)
        };
        var result = await _endSession.BeginAsync(new EndSessionRequest
        {
            IdTokenHint = Value("id_token_hint"),
            ClientId = Value("client_id"),
            PostLogoutRedirectUri = Value("post_logout_redirect_uri"),
            State = Value("state")
        }, caller, ct);
        if (!result.IsSuccess)
            return BadRequest(ApiResponse<object>.Fail(result.ErrorCode, result.Message));
        if (result.Data!.SessionEnded)
            await SignOutHostedLoginAsync();
        return Redirect(result.Data.RedirectUrl);
    }

    // A cross-site form post does not carry the SameSite=Lax session cookie, so the parameters move
    // to a top-level GET, which does.
    [HttpPost("logout")]
    [Consumes("application/x-www-form-urlencoded")]
    public async Task<IActionResult> EndSessionPost(CancellationToken ct)
    {
        var form = await Request.ReadFormAsync(ct);
        var query = QueryString.Create(form
            .Where(item => item.Key is "id_token_hint" or "client_id" or "post_logout_redirect_uri" or "state" or "ui_locales" or "logout_hint")
            .Select(item => new KeyValuePair<string, string?>(item.Key, item.Value.ToString())));
        Response.Headers.Location = $"/oauth/logout{query}";
        return StatusCode(StatusCodes.Status303SeeOther);
    }

    [AllowAnonymous]
    [HttpGet("logout/{logoutId}")]
    public async Task<IActionResult> GetPendingLogout(string logoutId, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        var result = await _endSession.GetPendingAsync(logoutId, BrowserBinding.Read(HttpContext), ct);
        return result.IsSuccess
            ? Ok(ApiResponse<object>.Ok(result.Data!))
            : NotFound(ApiResponse<object>.Fail(result.ErrorCode, result.Message));
    }

    /// <summary>The user confirmed the sign-out on the hosted logout page.</summary>
    [AllowAnonymous]
    [HttpPost("logout/{logoutId}/confirm")]
    public async Task<IActionResult> ConfirmLogout(string logoutId, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        var cookie = string.Equals(User.Identity?.AuthenticationType, AuthenticationSchemes.UiCookie, StringComparison.Ordinal);
        var result = await _endSession.ConfirmAsync(logoutId, new EndSessionCaller
        {
            UserId = cookie ? SessionClaims.UserId(User) : null,
            SessionId = cookie ? SessionClaims.SessionId(User) : null,
            BrowserBinding = BrowserBinding.Read(HttpContext)
        }, ct);
        if (!result.IsSuccess)
            return BadRequest(ApiResponse<object>.Fail(result.ErrorCode, result.Message));
        if (result.Data!.SessionEnded)
            await SignOutHostedLoginAsync();
        return Ok(ApiResponse<object>.Ok(new { redirectUrl = result.Data.RedirectUrl }));
    }

    private async Task SignOutHostedLoginAsync()
    {
        await HttpContext.SignOutAsync(AuthenticationSchemes.UiCookie);
        Response.Cookies.Delete(Middleware.UiCsrfMiddleware.CookieName, new CookieOptions { Secure = true, SameSite = SameSiteMode.Strict, Path = "/" });
    }

    [HttpPost("token")]
    [EnableRateLimiting(RateLimitingExtensions.OAuthToken)]
    [Consumes("application/x-www-form-urlencoded")]
    public async Task<IActionResult> Token(CancellationToken ct)
    {
        var form = await Request.ReadFormAsync(ct);
        var credentials = ReadClientCredentials(form);
        if (!credentials.IsValid)
            return OAuthError("invalid_client", credentials.Error!, StatusCodes.Status401Unauthorized);

        var request = new OAuthTokenRequest
        {
            GrantType = form["grant_type"],
            Code = form["code"],
            RedirectUri = form["redirect_uri"],
            ClientId = credentials.ClientId,
            ClientSecret = credentials.ClientSecret,
            CodeVerifier = form["code_verifier"],
            Scope = form["scope"],
            RefreshToken = form["refresh_token"]
        };

        OperationResult<OAuthTokenResponse> result = request.GrantType switch
        {
            "authorization_code" => await _oAuthService.ExchangeCodeAsync(request, ct),
            "client_credentials" => await _oAuthService.ClientCredentialsAsync(request, ct),
            "refresh_token" => await _oAuthService.RefreshOAuthTokenAsync(request, ct),
            _ => OperationResult<OAuthTokenResponse>.Failure(
                "UNSUPPORTED_GRANT_TYPE",
                $"Grant type '{request.GrantType}' is not supported.")
        };

        if (!result.IsSuccess)
            return OAuthError(
                result.ErrorCode.ToLowerInvariant(),
                result.Message,
                result.ErrorCode is "INVALID_CLIENT" or "INVALID_CLIENT_CREDENTIALS"
                    ? StatusCodes.Status401Unauthorized
                    : StatusCodes.Status400BadRequest);

        return Ok(result.Data!);
    }

    [HttpPost("revoke")]
    [EnableRateLimiting(RateLimitingExtensions.OAuthToken)]
    [Consumes("application/x-www-form-urlencoded")]
    public async Task<IActionResult> Revoke(CancellationToken ct)
    {
        var form = await Request.ReadFormAsync(ct);
        var credentials = ReadClientCredentials(form);
        if (!credentials.IsValid)
            return OAuthError("invalid_client", credentials.Error!, StatusCodes.Status401Unauthorized);

        var result = await _oAuthService.RevokeTokenAsync(new OAuthRevocationRequest
        {
            Token = form["token"],
            TokenTypeHint = form["token_type_hint"],
            ClientId = credentials.ClientId,
            ClientSecret = credentials.ClientSecret
        }, ct);

        if (!result.IsSuccess)
            return OAuthError(
                result.ErrorCode.ToLowerInvariant(),
                result.Message,
                result.ErrorCode is "INVALID_CLIENT" or "INVALID_CLIENT_CREDENTIALS"
                    ? StatusCodes.Status401Unauthorized
                    : StatusCodes.Status400BadRequest);

        return Ok();
    }

    [Authorize(AuthenticationSchemes = AuthenticationSchemes.OAuthBearer)]
    [HttpGet("userinfo")]
    public async Task<IActionResult> UserInfo(CancellationToken ct)
    {
        // Only tokens minted by the token endpoint carry client_id; this keeps first-party login
        // tokens, which are not audience-scoped to a client, out of the OAuth userinfo endpoint.
        if (User.FindFirst("client_id") is null) return Unauthorized();

        var userId = _currentUserService.UserId;
        if (userId is null) return Unauthorized();

        var scopeClaim = User.FindFirst("scope")?.Value ?? string.Empty;
        var scopes = scopeClaim.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var clientId = User.FindFirst("client_id")?.Value;
        if (string.IsNullOrWhiteSpace(clientId)) return Unauthorized();

        var result = await _oAuthService.GetUserInfoAsync(userId.Value, clientId, scopes, ct);
        if (!result.IsSuccess)
            return Unauthorized(ApiResponse<object>.Fail(result.ErrorCode, result.Message));
        return Ok(result.Data!);
    }

    private (bool IsValid, string? ClientId, string? ClientSecret, string? Error) ReadClientCredentials(IFormCollection form)
    {
        var formClientId = form["client_id"].ToString();
        var formClientSecret = form["client_secret"].ToString();
        var authorization = Request.Headers.Authorization.ToString();

        if (string.IsNullOrWhiteSpace(authorization))
            return (true, formClientId, formClientSecret, null);

        if (!authorization.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            return (false, null, null, "Only HTTP Basic client authentication is supported.");

        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(authorization[6..].Trim()));
            var separator = decoded.IndexOf(':');
            if (separator <= 0)
                return (false, null, null, "Malformed HTTP Basic client credentials.");

            var basicClientId = Uri.UnescapeDataString(decoded[..separator]);
            var basicSecret = Uri.UnescapeDataString(decoded[(separator + 1)..]);
            if (!string.IsNullOrWhiteSpace(formClientId) && !string.Equals(formClientId, basicClientId, StringComparison.Ordinal))
                return (false, null, null, "Conflicting client_id credentials were supplied.");
            if (!string.IsNullOrWhiteSpace(formClientSecret))
                return (false, null, null, "Use only one client authentication method per request.");

            return (true, basicClientId, basicSecret, null);
        }
        catch (Exception exception) when (exception is FormatException or UriFormatException)
        {
            return (false, null, null, "Malformed HTTP Basic client credentials.");
        }
    }

    private AuthorizationCaller CurrentCaller() => new()
    {
        UserId = _currentUserService.UserId,
        SessionId = SessionClaims.SessionId(User),
        BrowserBinding = BrowserBinding.Read(HttpContext),
        RequiresBrowserBinding = string.Equals(User.Identity?.AuthenticationType, AuthenticationSchemes.UiCookie, StringComparison.Ordinal),
        IpAddress = HttpContext.Connection.RemoteIpAddress?.ToString(),
        UserAgent = Request.Headers.UserAgent.ToString()
    };

    private ObjectResult OAuthError(string error, string description, int statusCode)
    {
        if (statusCode == StatusCodes.Status401Unauthorized)
            Response.Headers.WWWAuthenticate = "Basic realm=\"oauth/token\"";

        return StatusCode(statusCode, new { error, error_description = description });
    }
}
