using AuthCenter.Api.Filters;
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
    private readonly IReauthenticationService _reauthentication;
    private readonly ICurrentUserService _currentUser;
    private readonly IAuditService _audit;

    public OAuthClientsController(
        IOAuthClientService oAuthClientService,
        IReauthenticationService reauthentication,
        ICurrentUserService currentUser,
        IAuditService audit)
    {
        _oAuthClientService = oAuthClientService;
        _reauthentication = reauthentication;
        _currentUser = currentUser;
        _audit = audit;
    }

    [HttpGet]
    [Authorize(Policy = DomainConstants.Permissions.OAuthClientsRead)]
    public async Task<IActionResult> GetAll([FromQuery] OAuthClientQuery query, CancellationToken ct)
    {
        var clients = await _oAuthClientService.GetAllAsync(query, ct);
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

    [Idempotent]
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
        var current = await _oAuthClientService.GetByClientIdAsync(clientId, ct);
        if (current is null)
            return NotFound(ApiResponse<object>.Fail("NOT_FOUND", "OAuth client not found."));
        if (current.IsActive != request.IsActive)
        {
            var purpose = request.IsActive ? "admin.oauth-client.activate" : "admin.oauth-client.deactivate";
            if (!await HasReauthenticationProofAsync(purpose, ct))
            {
                await AuditRejectedAsync(
                    request.IsActive ? "OAUTH_CLIENT_ACTIVATION_REJECTED" : "OAUTH_CLIENT_DEACTIVATION_REJECTED",
                    clientId,
                    purpose,
                    ct);
                return ReauthenticationRequired(purpose);
            }
        }

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
        const string purpose = "admin.oauth-client.deactivate";
        if (!await HasReauthenticationProofAsync(purpose, ct))
        {
            await AuditRejectedAsync("OAUTH_CLIENT_DEACTIVATION_REJECTED", clientId, purpose, ct);
            return ReauthenticationRequired(purpose);
        }

        var result = await _oAuthClientService.DeactivateAsync(clientId, ct);
        if (!result.IsSuccess)
            return result.ErrorCode == "NOT_FOUND"
                ? NotFound(ApiResponse.Fail(result.ErrorCode, result.Message))
                : BadRequest(ApiResponse.Fail(result.ErrorCode, result.Message));
        return Ok(ApiResponse.Ok());
    }

    [Idempotent]
    [HttpPost("{clientId}/rotate-secret")]
    [Authorize(Policy = DomainConstants.Permissions.OAuthClientsWrite)]
    public async Task<IActionResult> RotateSecret(string clientId, CancellationToken ct)
    {
        const string purpose = "admin.oauth-client.rotate-secret";
        if (!await HasReauthenticationProofAsync(purpose, ct))
        {
            await AuditRejectedAsync("OAUTH_CLIENT_SECRET_ROTATION_REJECTED", clientId, purpose, ct);
            return ReauthenticationRequired(purpose);
        }

        var result = await _oAuthClientService.RotateSecretAsync(clientId, ct);
        if (!result.IsSuccess)
            return result.ErrorCode == "NOT_FOUND"
                ? NotFound(ApiResponse<object>.Fail(result.ErrorCode, result.Message))
                : BadRequest(ApiResponse<object>.Fail(result.ErrorCode, result.Message));
        return Ok(ApiResponse<object>.Ok(result.Data!));
    }

    private async Task<bool> HasReauthenticationProofAsync(string purpose, CancellationToken ct)
    {
        var currentUserId = _currentUser.UserId;
        return currentUserId.HasValue && await _reauthentication.ConsumeProofAsync(
            currentUserId.Value, purpose, Request.Headers["X-AuthCenter-Reauthentication"].ToString(), ct);
    }

    private Task AuditRejectedAsync(string action, string clientId, string purpose, CancellationToken ct) =>
        _audit.LogAsync(
            action,
            userId: _currentUser.UserId,
            entityName: "OAuthClient",
            entityId: clientId,
            ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(),
            userAgent: Request.Headers.UserAgent.ToString(),
            metadata: new { result = "Rejected", reason = "ReauthenticationRequired", purpose, traceId = HttpContext.TraceIdentifier },
            ct: ct);

    private ObjectResult ReauthenticationRequired(string purpose) => StatusCode(
        StatusCodes.Status403Forbidden,
        ApiResponse.Fail("REAUTHENTICATION_REQUIRED", $"A recent single-use reauthentication proof for {purpose} is required."));
}
