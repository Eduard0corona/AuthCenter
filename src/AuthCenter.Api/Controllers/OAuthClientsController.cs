using AuthCenter.Application.Interfaces;
using AuthCenter.Contracts.Requests.OAuth;
using AuthCenter.Contracts.Responses;
using AuthCenter.Domain.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuthCenter.Api.Controllers;

[ApiController]
[Route("api/oauth/clients")]
[Authorize]
public class OAuthClientsController : ControllerBase
{
    private readonly IOAuthClientService _oAuthClientService;

    public OAuthClientsController(IOAuthClientService oAuthClientService)
    {
        _oAuthClientService = oAuthClientService;
    }

    [HttpGet]
    [Authorize(Policy = DomainConstants.Permissions.OAuthClientsRead)]
    public async Task<IActionResult> GetAll(CancellationToken ct)
    {
        var clients = await _oAuthClientService.GetAllAsync(ct);
        return Ok(ApiResponse<object>.Ok(clients));
    }

    [HttpGet("{clientId}")]
    [Authorize(Policy = DomainConstants.Permissions.OAuthClientsRead)]
    public async Task<IActionResult> GetByClientId(string clientId, CancellationToken ct)
    {
        var client = await _oAuthClientService.GetByClientIdAsync(clientId, ct);
        if (client is null) return NotFound(ApiResponse<object>.Fail("NOT_FOUND", "OAuth client not found."));
        return Ok(ApiResponse<object>.Ok(client));
    }

    [HttpPost]
    [Authorize(Policy = DomainConstants.Permissions.OAuthClientsWrite)]
    public async Task<IActionResult> Create([FromBody] CreateOAuthClientRequest request, CancellationToken ct)
    {
        var result = await _oAuthClientService.CreateAsync(request, ct);
        if (!result.IsSuccess)
            return BadRequest(ApiResponse<object>.Fail(result.ErrorCode, result.Message));
        return CreatedAtAction(nameof(GetByClientId), new { clientId = result.Data!.Client.ClientId }, ApiResponse<object>.Ok(result.Data));
    }

    [HttpPut("{clientId}")]
    [Authorize(Policy = DomainConstants.Permissions.OAuthClientsWrite)]
    public async Task<IActionResult> Update(string clientId, [FromBody] UpdateOAuthClientRequest request, CancellationToken ct)
    {
        var result = await _oAuthClientService.UpdateAsync(clientId, request, ct);
        if (!result.IsSuccess)
            return result.ErrorCode == "NOT_FOUND"
                ? NotFound(ApiResponse<object>.Fail(result.ErrorCode, result.Message))
                : BadRequest(ApiResponse<object>.Fail(result.ErrorCode, result.Message));
        return Ok(ApiResponse<object>.Ok(result.Data!));
    }

    [HttpDelete("{clientId}")]
    [Authorize(Policy = DomainConstants.Permissions.OAuthClientsWrite)]
    public async Task<IActionResult> Deactivate(string clientId, CancellationToken ct)
    {
        var result = await _oAuthClientService.DeactivateAsync(clientId, ct);
        if (!result.IsSuccess)
            return result.ErrorCode == "NOT_FOUND"
                ? NotFound(ApiResponse.Fail(result.ErrorCode, result.Message))
                : BadRequest(ApiResponse.Fail(result.ErrorCode, result.Message));
        return Ok(ApiResponse.Ok());
    }

    [HttpPost("{clientId}/rotate-secret")]
    [Authorize(Policy = DomainConstants.Permissions.OAuthClientsWrite)]
    public async Task<IActionResult> RotateSecret(string clientId, CancellationToken ct)
    {
        var result = await _oAuthClientService.RotateSecretAsync(clientId, ct);
        if (!result.IsSuccess)
            return result.ErrorCode == "NOT_FOUND"
                ? NotFound(ApiResponse<object>.Fail(result.ErrorCode, result.Message))
                : BadRequest(ApiResponse<object>.Fail(result.ErrorCode, result.Message));
        return Ok(ApiResponse<object>.Ok(result.Data!));
    }
}
