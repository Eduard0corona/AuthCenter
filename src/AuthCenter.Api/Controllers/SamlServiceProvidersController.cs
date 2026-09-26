using AuthCenter.Api.Filters;
using AuthCenter.Application.Common;
using AuthCenter.Application.Interfaces;
using AuthCenter.Contracts.Requests.Saml;
using AuthCenter.Contracts.Responses;
using AuthCenter.Domain.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuthCenter.Api.Controllers;

/// <summary>The applications that sign in with SAML, AuthCenter being their identity provider.</summary>
[ApiController]
[Authorize]
[Route("api/saml")]
public sealed class SamlServiceProvidersController : ControllerBase
{
    private readonly ISamlServiceProviderService _providers;

    public SamlServiceProvidersController(ISamlServiceProviderService providers) => _providers = providers;

    /// <summary>What to register in a service provider: entity ID, URLs, signing certificate.</summary>
    [HttpGet("identity-provider")]
    [Authorize(Policy = DomainConstants.Permissions.SamlAppsRead)]
    public IActionResult IdentityProvider() => Ok(ApiResponse<object>.Ok(_providers.DescribeIdentityProvider()));

    [HttpGet("service-providers")]
    [Authorize(Policy = DomainConstants.Permissions.SamlAppsRead)]
    public async Task<IActionResult> Get([FromQuery] SamlServiceProviderQuery query, CancellationToken ct) =>
        Ok(ApiResponse<object>.Ok(await _providers.GetAsync(query, ct)));

    [HttpGet("service-providers/{id:guid}")]
    [Authorize(Policy = DomainConstants.Permissions.SamlAppsRead)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var provider = await _providers.GetByIdAsync(id, ct);
        return provider is null ? NotFound(ApiResponse<object>.Fail("SAML_SP_NOT_FOUND", "SAML application not found.")) : Ok(ApiResponse<object>.Ok(provider));
    }

    [HttpPost("service-providers")]
    [Idempotent]
    [Authorize(Policy = DomainConstants.Permissions.SamlAppsWrite)]
    public async Task<IActionResult> Create(CreateSamlServiceProviderRequest request, CancellationToken ct)
    {
        var result = await _providers.CreateAsync(request, ct);
        return result.IsSuccess
            ? CreatedAtAction(nameof(GetById), new { id = result.Data!.Id }, ApiResponse<object>.Ok(result.Data))
            : Failure(result);
    }

    [HttpPut("service-providers/{id:guid}")]
    [Authorize(Policy = DomainConstants.Permissions.SamlAppsWrite)]
    public async Task<IActionResult> Update(Guid id, UpdateSamlServiceProviderRequest request, CancellationToken ct)
    {
        var result = await _providers.UpdateAsync(id, request, ct);
        return result.IsSuccess ? Ok(ApiResponse<object>.Ok(result.Data!)) : Failure(result);
    }

    [HttpDelete("service-providers/{id:guid}")]
    [Authorize(Policy = DomainConstants.Permissions.SamlAppsWrite)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var result = await _providers.DeleteAsync(id, ct);
        return result.IsSuccess
            ? Ok(ApiResponse.Ok())
            : result.ErrorCode == "SAML_SP_NOT_FOUND" ? NotFound(ApiResponse.Fail(result.ErrorCode, result.Message)) : BadRequest(ApiResponse.Fail(result.ErrorCode, result.Message));
    }

    /// <summary>Reads a service provider's metadata to fill the form; nothing is saved.</summary>
    [HttpPost("service-providers/parse-metadata")]
    [Authorize(Policy = DomainConstants.Permissions.SamlAppsWrite)]
    public IActionResult ParseMetadata(ParseSamlMetadataRequest request)
    {
        var result = _providers.ParseMetadata(request);
        return result.IsSuccess ? Ok(ApiResponse<object>.Ok(result.Data!)) : BadRequest(ApiResponse<object>.Fail(result.ErrorCode, result.Message));
    }

    private IActionResult Failure<T>(OperationResult<T> result) => result.ErrorCode switch
    {
        "SAML_SP_NOT_FOUND" => NotFound(ApiResponse<object>.Fail(result.ErrorCode, result.Message)),
        "CONCURRENCY_CONFLICT" or "SAML_SP_EXISTS" => Conflict(ApiResponse<object>.Fail(result.ErrorCode, result.Message)),
        _ => BadRequest(ApiResponse<object>.Fail(result.ErrorCode, result.Message))
    };
}
