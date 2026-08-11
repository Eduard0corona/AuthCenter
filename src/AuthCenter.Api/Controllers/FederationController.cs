using AuthCenter.Application.Interfaces;
using AuthCenter.Contracts.Requests.Federation;
using AuthCenter.Contracts.Responses;
using AuthCenter.Domain.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text;

namespace AuthCenter.Api.Controllers;

[ApiController]
[Route("api/federation")]
public sealed class FederationController : ControllerBase
{
    private readonly IFederationService _federation;
    public FederationController(IFederationService federation) => _federation = federation;

    [Authorize(Policy = DomainConstants.Permissions.ApplicationsRead)]
    [HttpGet("providers")]
    public async Task<IActionResult> GetProviders([FromQuery] Guid? applicationSystemId, CancellationToken ct) =>
        Ok(ApiResponse<object>.Ok(await _federation.GetProvidersAsync(applicationSystemId, ct)));

    [Authorize(Policy = DomainConstants.Permissions.ApplicationsWrite)]
    [HttpPost("providers")]
    public async Task<IActionResult> CreateProvider(UpsertFederationProviderRequest request, CancellationToken ct)
    {
        var result = await _federation.CreateProviderAsync(request, ct);
        return result.IsSuccess ? Created($"/api/federation/providers/{result.Data!.Id}", ApiResponse<object>.Ok(result.Data)) : BadRequest(ApiResponse<object>.Fail(result.ErrorCode, result.Message));
    }

    [Authorize(Policy = DomainConstants.Permissions.ApplicationsWrite)]
    [HttpPut("providers/{id:guid}")]
    public async Task<IActionResult> UpdateProvider(Guid id, UpsertFederationProviderRequest request, CancellationToken ct)
    {
        var result = await _federation.UpdateProviderAsync(id, request, ct);
        return result.IsSuccess ? Ok(ApiResponse<object>.Ok(result.Data!)) : BadRequest(ApiResponse<object>.Fail(result.ErrorCode, result.Message));
    }

    [Authorize(Policy = DomainConstants.Permissions.ApplicationsWrite)]
    [HttpDelete("providers/{id:guid}")]
    public async Task<IActionResult> DeleteProvider(Guid id, CancellationToken ct)
    {
        var result = await _federation.DeleteProviderAsync(id, ct);
        return result.IsSuccess ? Ok(ApiResponse.Ok()) : BadRequest(ApiResponse.Fail(result.ErrorCode, result.Message));
    }

    [Authorize(Policy = DomainConstants.Permissions.ApplicationsWrite)]
    [HttpPost("routing-rules")]
    public async Task<IActionResult> CreateRoutingRule(CreateFederationRoutingRuleRequest request, CancellationToken ct)
    {
        var result = await _federation.CreateRoutingRuleAsync(request, ct);
        return result.IsSuccess ? Ok(ApiResponse.Ok()) : BadRequest(ApiResponse.Fail(result.ErrorCode, result.Message));
    }

    [HttpPost("route")]
    public async Task<IActionResult> Route(FederationRouteRequest request, CancellationToken ct)
    {
        var result = await _federation.RouteAsync(request, ct);
        return result.IsSuccess ? Ok(ApiResponse<object>.Ok(result.Data!)) : NotFound(ApiResponse<object>.Fail(result.ErrorCode, result.Message));
    }

    [HttpPost("oidc/begin")]
    public async Task<IActionResult> BeginOidc(BeginOidcFederationRequest request, CancellationToken ct)
    {
        var result = await _federation.BeginOidcAsync(request, ct);
        return result.IsSuccess ? Ok(ApiResponse<object>.Ok(result.Data!)) : BadRequest(ApiResponse<object>.Fail(result.ErrorCode, result.Message));
    }

    [HttpPost("oidc/complete")]
    public async Task<IActionResult> CompleteOidc(CompleteOidcFederationRequest request, CancellationToken ct)
    {
        var result = await _federation.CompleteOidcAsync(request, HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString(), ct);
        return result.IsSuccess ? Ok(ApiResponse<object>.Ok(result.Data!)) : Unauthorized(ApiResponse<object>.Fail(result.ErrorCode, result.Message));
    }

    [HttpPost("saml/begin")]
    public async Task<IActionResult> BeginSaml(BeginSamlFederationRequest request, CancellationToken ct)
    {
        var result = await _federation.BeginSamlAsync(request, ct);
        return result.IsSuccess ? Ok(ApiResponse<object>.Ok(result.Data!)) : BadRequest(ApiResponse<object>.Fail(result.ErrorCode, result.Message));
    }

    [Consumes("application/x-www-form-urlencoded")]
    [HttpPost("saml/acs")]
    public async Task<IActionResult> CompleteSaml([FromForm] CompleteSamlFederationRequest request, CancellationToken ct)
    {
        var result = await _federation.CompleteSamlAsync(request, HttpContext.Connection.RemoteIpAddress?.ToString(), Request.Headers.UserAgent.ToString(), ct);
        return result.IsSuccess ? Ok(ApiResponse<object>.Ok(result.Data!)) : Unauthorized(ApiResponse<object>.Fail(result.ErrorCode, result.Message));
    }

    [HttpGet("saml/{providerId:guid}/metadata")]
    public async Task<IActionResult> SamlMetadata(Guid providerId, CancellationToken ct)
    {
        var result = await _federation.GetSamlMetadataAsync(providerId, ct);
        return result.IsSuccess ? Content(result.Data!, "application/samlmetadata+xml", Encoding.UTF8) : NotFound(ApiResponse.Fail(result.ErrorCode, result.Message));
    }
}
