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
    private readonly IEventHookService _hooks; public EventHooksController(IEventHookService hooks) => _hooks = hooks;
    [HttpGet, Authorize(Policy = DomainConstants.Permissions.ApplicationsRead)] public async Task<IActionResult> Get([FromQuery] EventHookQuery query, CancellationToken ct) => Ok(ApiResponse<object>.Ok(await _hooks.GetAsync(query, ct)));
    [HttpGet("{id:guid}"), Authorize(Policy = DomainConstants.Permissions.ApplicationsRead)] public async Task<IActionResult> GetById(Guid id, CancellationToken ct) { var item = await _hooks.GetByIdAsync(id, ct); return item is null ? NotFound(ApiResponse<object>.Fail("EVENT_HOOK_NOT_FOUND", "Event hook not found.")) : Ok(ApiResponse<object>.Ok(item)); }
    [HttpPost, Authorize(Policy = DomainConstants.Permissions.ApplicationsWrite)] public async Task<IActionResult> Create(CreateEventHookRequest request, CancellationToken ct) { var r = await _hooks.CreateAsync(request, ct); return r.IsSuccess ? Created($"/api/event-hooks/{r.Data!.Id}", ApiResponse<object>.Ok(r.Data)) : BadRequest(ApiResponse<object>.Fail(r.ErrorCode, r.Message)); }
    [HttpPut("{id:guid}"), Authorize(Policy = DomainConstants.Permissions.ApplicationsWrite)] public async Task<IActionResult> Update(Guid id, UpdateEventHookRequest request, CancellationToken ct) { var r = await _hooks.UpdateAsync(id, request, ct); return Map(r); }
    [HttpPost("{id:guid}/verify"), Authorize(Policy = DomainConstants.Permissions.ApplicationsWrite)] public async Task<IActionResult> Verify(Guid id, CancellationToken ct) => Map(await _hooks.VerifyAsync(id, ct));
    [HttpDelete("{id:guid}"), Authorize(Policy = DomainConstants.Permissions.ApplicationsWrite)] public async Task<IActionResult> Delete(Guid id, CancellationToken ct) => Map(await _hooks.DeleteAsync(id, ct));
    [HttpPost("deliveries/{id:guid}/replay"), Authorize(Policy = DomainConstants.Permissions.ApplicationsWrite)] public async Task<IActionResult> Replay(Guid id, CancellationToken ct) { var key = Request.Headers["Idempotency-Key"].ToString(); if (string.IsNullOrWhiteSpace(key)) key = $"legacy:{id:N}"; return Map(await _hooks.ReplayDeadLetterAsync(id, key, ct)); }
    [HttpGet("deliveries"), Authorize(Policy = DomainConstants.Permissions.ApplicationsRead)]
    public async Task<IActionResult> Deliveries([FromQuery] EventHookDeliveryQuery query, [FromQuery] bool? deadLettersOnly, CancellationToken ct)
    {
        if (!deadLettersOnly.HasValue)
            return Ok(ApiResponse<object>.Ok(await _hooks.GetDeliveriesAsync(query, ct)));

        // Legacy array shape kept for the retiring /admin console. Without an explicit page size it
        // returns the largest page and always reports the full count so truncation is visible.
        var pageSize = Request.Query.ContainsKey("pageSize") ? query.PageSize : 100;
        var legacy = new EventHookDeliveryQuery { Page = query.Page, PageSize = pageSize, HookId = query.HookId, EventId = query.EventId, EventType = query.EventType, FromUtc = query.FromUtc, ToUtc = query.ToUtc, Status = deadLettersOnly.Value ? "dead-letter" : query.Status };
        var result = await _hooks.GetDeliveriesAsync(legacy, ct);
        Response.Headers["X-Total-Count"] = result.TotalCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Ok(ApiResponse<object>.Ok(result.Items));
    }
    private IActionResult Map(OperationResult r) => r.IsSuccess ? Ok(ApiResponse.Ok()) : Failure(r.ErrorCode, r.Message);
    private IActionResult Map<T>(OperationResult<T> r) => r.IsSuccess ? Ok(ApiResponse<object>.Ok(r.Data!)) : Failure(r.ErrorCode, r.Message);
    private IActionResult Failure(string code, string message) => code == "CONCURRENCY_CONFLICT" ? Conflict(ApiResponse.Fail(code, message)) : BadRequest(ApiResponse.Fail(code, message));
}
