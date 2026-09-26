using AuthCenter.Application.Common;
using AuthCenter.Application.Interfaces;
using AuthCenter.Contracts.Requests.Lifecycle;
using AuthCenter.Contracts.Responses;
using AuthCenter.Domain.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace AuthCenter.Api.Controllers;

[ApiController, Authorize, Route("api/event-hooks")]
public sealed class EventHooksController : ControllerBase
{
    private const string RotateSecretPurpose = "admin.event-hook.rotate-secret";
    private readonly IEventHookService _hooks; public EventHooksController(IEventHookService hooks) => _hooks = hooks;
    [HttpGet, Authorize(Policy = DomainConstants.Permissions.EventHooksRead)] public async Task<IActionResult> Get([FromQuery] EventHookQuery query, CancellationToken ct) => Ok(ApiResponse<object>.Ok(await _hooks.GetAsync(query, ct)));
    [HttpGet("{id:guid}"), Authorize(Policy = DomainConstants.Permissions.EventHooksRead)] public async Task<IActionResult> GetById(Guid id, CancellationToken ct) { var item = await _hooks.GetByIdAsync(id, ct); return item is null ? NotFound(ApiResponse<object>.Fail("EVENT_HOOK_NOT_FOUND", "Event hook not found.")) : Ok(ApiResponse<object>.Ok(item)); }
    [HttpPost, Authorize(Policy = DomainConstants.Permissions.EventHooksWrite)] public async Task<IActionResult> Create(CreateEventHookRequest request, CancellationToken ct) { var r = await _hooks.CreateAsync(request, ct); return r.IsSuccess ? Created($"/api/event-hooks/{r.Data!.Id}", ApiResponse<object>.Ok(r.Data)) : BadRequest(ApiResponse<object>.Fail(r.ErrorCode, r.Message)); }
    [HttpPut("{id:guid}"), Authorize(Policy = DomainConstants.Permissions.EventHooksWrite)] public async Task<IActionResult> Update(Guid id, UpdateEventHookRequest request, CancellationToken ct) { var r = await _hooks.UpdateAsync(id, request, ct); return Map(r); }
    [HttpPost("{id:guid}/verify"), Authorize(Policy = DomainConstants.Permissions.EventHooksWrite)] public async Task<IActionResult> Verify(Guid id, CancellationToken ct) => Map(await _hooks.VerifyAsync(id, ct));
    [HttpDelete("{id:guid}"), Authorize(Policy = DomainConstants.Permissions.EventHooksWrite)] public async Task<IActionResult> Delete(Guid id, CancellationToken ct) => Map(await _hooks.DeleteAsync(id, ct));
    /// <summary>The event types a hook can subscribe to ("*" subscribes to all).</summary>
    [HttpGet("event-types"), Authorize(Policy = DomainConstants.Permissions.EventHooksRead)] public IActionResult EventTypes() => Ok(ApiResponse<object>.Ok(_hooks.GetEventTypes()));
    /// <summary>Returns the new signing secret once; deliveries are signed with both secrets for 24 hours.</summary>
    [HttpPost("{id:guid}/rotate-secret"), Authorize(Policy = DomainConstants.Permissions.EventHooksWrite)]
    public async Task<IActionResult> RotateSecret(Guid id, [FromServices] IReauthenticationService reauthentication, [FromServices] ICurrentUserService currentUser, CancellationToken ct)
    {
        if (currentUser.UserId is not { } userId || !await reauthentication.ConsumeProofAsync(userId, RotateSecretPurpose, Request.Headers["X-AuthCenter-Reauthentication"].ToString(), ct))
            return StatusCode(StatusCodes.Status403Forbidden, ApiResponse.Fail("REAUTHENTICATION_REQUIRED", $"A recent single-use reauthentication proof for {RotateSecretPurpose} is required."));
        return Map(await _hooks.RotateSecretAsync(id, ct));
    }
    [HttpGet("deliveries/{id:guid}"), Authorize(Policy = DomainConstants.Permissions.EventHooksRead)] public async Task<IActionResult> Delivery(Guid id, CancellationToken ct) { var item = await _hooks.GetDeliveryAsync(id, ct); return item is null ? NotFound(ApiResponse<object>.Fail("DELIVERY_NOT_FOUND", "Delivery not found.")) : Ok(ApiResponse<object>.Ok(item)); }
    [HttpPost("deliveries/{id:guid}/replay"), Authorize(Policy = DomainConstants.Permissions.EventHooksWrite)] public async Task<IActionResult> Replay(Guid id, CancellationToken ct) { var key = Request.Headers["Idempotency-Key"].ToString(); if (string.IsNullOrWhiteSpace(key)) key = $"legacy:{id:N}"; return Map(await _hooks.ReplayDeadLetterAsync(id, key, ct)); }
    [HttpGet("deliveries"), Authorize(Policy = DomainConstants.Permissions.EventHooksRead)]
    public async Task<IActionResult> Deliveries([FromQuery] EventHookDeliveryQuery query, CancellationToken ct) => Ok(ApiResponse<object>.Ok(await _hooks.GetDeliveriesAsync(query, ct)));
    private IActionResult Map(OperationResult r) => r.IsSuccess ? Ok(ApiResponse.Ok()) : Failure(r.ErrorCode, r.Message);
    private IActionResult Map<T>(OperationResult<T> r) => r.IsSuccess ? Ok(ApiResponse<object>.Ok(r.Data!)) : Failure(r.ErrorCode, r.Message);
    private IActionResult Failure(string code, string message) => code == "CONCURRENCY_CONFLICT" ? Conflict(ApiResponse.Fail(code, message)) : BadRequest(ApiResponse.Fail(code, message));
}
