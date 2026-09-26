using AuthCenter.Api.Filters;
using AuthCenter.Api.Extensions;
using AuthCenter.Api.Middleware;
using AuthCenter.Application.Common;
using AuthCenter.Application.Interfaces;
using AuthCenter.Contracts.Requests.Federation;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Responses.Auth;
using AuthCenter.Domain.Constants;
using AuthCenter.Infrastructure.Settings;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using System.Text;

namespace AuthCenter.Api.Controllers;

[ApiController]
[Route("api/federation")]
public sealed class FederationController : ControllerBase
{
    private readonly IFederationService _federation;
    private readonly IReauthenticationService _reauthentication;
    private readonly ICurrentUserService _currentUser;
    private readonly IAuditService _audit;
    private readonly MfaSettings _mfa;
    public FederationController(IFederationService federation, IReauthenticationService reauthentication, ICurrentUserService currentUser, IAuditService audit, IOptions<MfaSettings> mfa) =>
        (_federation, _reauthentication, _currentUser, _audit, _mfa) = (federation, reauthentication, currentUser, audit, mfa.Value);

    /// <summary>Values to register at upstream providers: the hosted OIDC callback, the SAML entity ID and ACS.</summary>
    [Authorize(Policy = DomainConstants.Permissions.FederationRead)]
    [HttpGet("service-provider")]
    public IActionResult ServiceProvider() => Ok(ApiResponse<object>.Ok(_federation.GetServiceProviderInfo()));

    /// <summary>Checks discovery, keys, certificates and callbacks of a provider without changing it.</summary>
    [Authorize(Policy = DomainConstants.Permissions.FederationWrite)]
    [HttpPost("providers/{id:guid}/test")]
    public async Task<IActionResult> TestProvider(Guid id, CancellationToken ct)
    {
        var result = await _federation.TestConnectionAsync(id, ct);
        return result.IsSuccess ? Ok(ApiResponse<object>.Ok(result.Data!)) : NotFound(ApiResponse<object>.Fail(result.ErrorCode, SignInMessages.ForApi(result.ErrorCode, result.Message)));
    }

    [Authorize(Policy = DomainConstants.Permissions.FederationRead)]
    [HttpGet("providers")]
    public async Task<IActionResult> GetProviders([FromQuery] Guid? applicationSystemId, CancellationToken ct) =>
        Ok(ApiResponse<object>.Ok(await _federation.GetProvidersAsync(applicationSystemId, ct)));

    [Authorize(Policy = DomainConstants.Permissions.FederationWrite)]
    [Idempotent]
    [HttpPost("providers")]
    public async Task<IActionResult> CreateProvider(UpsertFederationProviderRequest request, CancellationToken ct)
    {
        if (!await HasProofAsync(ct)) return await RejectedAsync("FederationProvider", null, ct);
        var result = await _federation.CreateProviderAsync(request, ct);
        return result.IsSuccess ? Created($"/api/federation/providers/{result.Data!.Id}", ApiResponse<object>.Ok(result.Data)) : BadRequest(ApiResponse<object>.Fail(result.ErrorCode, SignInMessages.ForApi(result.ErrorCode, result.Message)));
    }

    [Authorize(Policy = DomainConstants.Permissions.FederationWrite)]
    [HttpPut("providers/{id:guid}")]
    public async Task<IActionResult> UpdateProvider(Guid id, UpsertFederationProviderRequest request, CancellationToken ct)
    {
        if (!await HasProofAsync(ct)) return await RejectedAsync("FederationProvider", id, ct);
        var result = await _federation.UpdateProviderAsync(id, request, ct);
        return result.IsSuccess ? Ok(ApiResponse<object>.Ok(result.Data!)) : result.ErrorCode == "CONCURRENCY_CONFLICT" ? Conflict(ApiResponse<object>.Fail(result.ErrorCode, SignInMessages.ForApi(result.ErrorCode, result.Message))) : BadRequest(ApiResponse<object>.Fail(result.ErrorCode, SignInMessages.ForApi(result.ErrorCode, result.Message)));
    }

    [Authorize(Policy = DomainConstants.Permissions.FederationWrite)]
    [HttpDelete("providers/{id:guid}")]
    public async Task<IActionResult> DeleteProvider(Guid id, CancellationToken ct)
    {
        if (!await HasProofAsync(ct)) return await RejectedAsync("FederationProvider", id, ct);
        var result = await _federation.DeleteProviderAsync(id, ct);
        return result.IsSuccess ? Ok(ApiResponse.Ok()) : BadRequest(ApiResponse.Fail(result.ErrorCode, result.Message));
    }

    [Authorize(Policy = DomainConstants.Permissions.FederationWrite)]
    [Idempotent]
    [HttpPost("routing-rules")]
    public async Task<IActionResult> CreateRoutingRule(CreateFederationRoutingRuleRequest request, CancellationToken ct)
    {
        if (!await HasProofAsync(ct)) return await RejectedAsync("FederationRoutingRule", null, ct);
        var result = await _federation.CreateRoutingRuleAsync(request, ct);
        return result.IsSuccess ? Ok(ApiResponse.Ok()) : BadRequest(ApiResponse.Fail(result.ErrorCode, result.Message));
    }

    [Authorize(Policy = DomainConstants.Permissions.FederationRead)]
    [HttpGet("routing-rules")]
    public async Task<IActionResult> GetRoutingRules([FromQuery] Guid? applicationSystemId, CancellationToken ct) =>
        Ok(ApiResponse<object>.Ok(await _federation.GetRoutingRulesAsync(applicationSystemId, ct)));

    [Authorize(Policy = DomainConstants.Permissions.FederationWrite)]
    [HttpPut("routing-rules/{id:guid}")]
    public async Task<IActionResult> UpdateRoutingRule(Guid id, UpdateFederationRoutingRuleRequest request, CancellationToken ct)
    {
        if (!await HasProofAsync(ct)) return await RejectedAsync("FederationRoutingRule", id, ct);
        var result = await _federation.UpdateRoutingRuleAsync(id, request, ct);
        if (result.IsSuccess) return Ok(ApiResponse<object>.Ok(result.Data!));
        return result.ErrorCode == "CONCURRENCY_CONFLICT" ? Conflict(ApiResponse<object>.Fail(result.ErrorCode, SignInMessages.ForApi(result.ErrorCode, result.Message))) : BadRequest(ApiResponse<object>.Fail(result.ErrorCode, SignInMessages.ForApi(result.ErrorCode, result.Message)));
    }

    [Authorize(Policy = DomainConstants.Permissions.FederationWrite)]
    [HttpPut("routing-rules/order")]
    public async Task<IActionResult> ReorderRoutingRules(ReorderFederationRoutingRulesRequest request, CancellationToken ct)
    {
        if (!await HasProofAsync(ct)) return await RejectedAsync("FederationRoutingRule", null, ct);
        var result = await _federation.ReorderRoutingRulesAsync(request, ct);
        if (result.IsSuccess) return Ok(ApiResponse.Ok());
        return result.ErrorCode == "CONCURRENCY_CONFLICT" ? Conflict(ApiResponse.Fail(result.ErrorCode, result.Message)) : BadRequest(ApiResponse.Fail(result.ErrorCode, result.Message));
    }

    [Authorize(Policy = DomainConstants.Permissions.FederationWrite)]
    [HttpDelete("routing-rules/{id:guid}")]
    public async Task<IActionResult> DeleteRoutingRule(Guid id, CancellationToken ct)
    {
        if (!await HasProofAsync(ct)) return await RejectedAsync("FederationRoutingRule", id, ct);
        var result = await _federation.DeleteRoutingRuleAsync(id, ct);
        return result.IsSuccess ? Ok(ApiResponse.Ok()) : BadRequest(ApiResponse.Fail(result.ErrorCode, result.Message));
    }

    /// <summary>
    /// Administrative route simulation. It evaluates group and profile conditions, so it is not
    /// anonymous; the hosted login uses <c>/ui-api/session/federation/discover</c> instead.
    /// </summary>
    [Authorize(Policy = DomainConstants.Permissions.FederationRead)]
    [HttpPost("route")]
    public async Task<IActionResult> Route(FederationRouteRequest request, CancellationToken ct)
    {
        var result = await _federation.RouteAsync(request, ct);
        return result.IsSuccess ? Ok(ApiResponse<object>.Ok(result.Data!)) : NotFound(ApiResponse<object>.Fail(result.ErrorCode, SignInMessages.ForApi(result.ErrorCode, result.Message)));
    }

    /// <summary>
    /// Server-side OIDC callback of the hosted login. It never signs the browser in by itself: it
    /// records a single-use result and sends the browser back to the hosted login, which redeems it.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("oidc/callback")]
    public async Task<IActionResult> OidcCallback([FromQuery] string? state, [FromQuery] string? code, [FromQuery] string? error, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        return SeeOther(await _federation.CompleteOidcCallbackAsync(state, code, error, IpAddress(), UserAgent(), ct));
    }

    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingExtensions.FederationStart)]
    [HttpPost("oidc/begin")]
    public async Task<IActionResult> BeginOidc(BeginOidcFederationRequest request, CancellationToken ct)
    {
        var result = await _federation.BeginOidcAsync(request, ct);
        return result.IsSuccess ? Ok(ApiResponse<object>.Ok(result.Data!)) : BadRequest(ApiResponse<object>.Fail(result.ErrorCode, SignInMessages.ForApi(result.ErrorCode, result.Message)));
    }

    [AllowAnonymous]
    [HttpPost("oidc/complete")]
    public async Task<IActionResult> CompleteOidc(CompleteOidcFederationRequest request, CancellationToken ct) =>
        SignInResult(await _federation.CompleteOidcAsync(request, IpAddress(), UserAgent(), ct));

    [AllowAnonymous]
    [EnableRateLimiting(RateLimitingExtensions.FederationStart)]
    [HttpPost("saml/begin")]
    public async Task<IActionResult> BeginSaml(BeginSamlFederationRequest request, CancellationToken ct)
    {
        var result = await _federation.BeginSamlAsync(request, ct);
        return result.IsSuccess ? Ok(ApiResponse<object>.Ok(result.Data!)) : BadRequest(ApiResponse<object>.Fail(result.ErrorCode, SignInMessages.ForApi(result.ErrorCode, result.Message)));
    }

    /// <summary>
    /// SAML assertion consumer service (HTTP-POST binding). A response to a hosted-login request
    /// continues in the hosted login (303); one started through the JSON API answers with JSON.
    /// </summary>
    [AllowAnonymous]
    // The response is authenticated by its signature and single-use RelayState, never by the
    // browser's cookie, which a same-site identity provider's form post still carries.
    [IgnoreUiCsrf]
    [Consumes("application/x-www-form-urlencoded")]
    [HttpPost("saml/acs")]
    public async Task<IActionResult> CompleteSaml([FromForm] CompleteSamlFederationRequest request, CancellationToken ct)
    {
        Response.Headers.CacheControl = "no-store";
        var completion = await _federation.CompleteSamlAsync(request, IpAddress(), UserAgent(), ct);
        return completion.RedirectPath is { } path ? SeeOther(path) : SignInResult(completion.Result!);
    }

    // 303 so the browser continues in the hosted login with a GET, whatever method brought it here.
    private StatusCodeResult SeeOther(string localPath)
    {
        Response.Headers.Location = localPath;
        return StatusCode(StatusCodes.Status303SeeOther);
    }

    [HttpGet("saml/{providerId:guid}/metadata")]
    public async Task<IActionResult> SamlMetadata(Guid providerId, CancellationToken ct)
    {
        var result = await _federation.GetSamlMetadataAsync(providerId, ct);
        return result.IsSuccess ? Content(result.Data!, "application/samlmetadata+xml", Encoding.UTF8) : NotFound(ApiResponse.Fail(result.ErrorCode, result.Message));
    }

    // JSON API sign-ins follow /api/auth/login: a pending second factor is not an error.
    private IActionResult SignInResult(OperationResult<AuthResponse> result)
    {
        if (!result.IsSuccess && result.ErrorCode == "MFA_REQUIRED")
            return Ok(ApiResponse<object>.Ok(new MfaPendingResponse { MfaPendingToken = result.Message, ExpiresIn = _mfa.MfaTokenExpirySeconds }));
        return result.IsSuccess ? Ok(ApiResponse<object>.Ok(result.Data!)) : Unauthorized(ApiResponse<object>.Fail(result.ErrorCode, SignInMessages.ForApi(result.ErrorCode, result.Message)));
    }

    private string? IpAddress() => HttpContext.Connection.RemoteIpAddress?.ToString();
    private string UserAgent() => Request.Headers.UserAgent.ToString();

    private Task<bool> HasProofAsync(CancellationToken ct) => _currentUser.UserId.HasValue
        ? _reauthentication.ConsumeProofAsync(_currentUser.UserId.Value, "admin.federation.change", Request.Headers["X-AuthCenter-Reauthentication"].ToString(), ct)
        : Task.FromResult(false);

    private async Task<ObjectResult> RejectedAsync(string entity, Guid? id, CancellationToken ct)
    {
        await _audit.LogAsync("FEDERATION_CHANGE_REJECTED", entityName: entity, entityId: id?.ToString(), metadata: new { result = "Rejected", reason = "ReauthenticationRequired", purpose = "admin.federation.change" }, ct: ct);
        return StatusCode(StatusCodes.Status403Forbidden, ApiResponse.Fail("REAUTHENTICATION_REQUIRED", "A recent single-use reauthentication proof for admin.federation.change is required."));
    }
}
