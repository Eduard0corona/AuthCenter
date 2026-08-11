using AuthCenter.Application.Interfaces;
using AuthCenter.Contracts.Requests.Lifecycle;
using AuthCenter.Contracts.Responses;
using AuthCenter.Domain.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace AuthCenter.Api.Controllers;

[ApiController, Authorize(Policy = DomainConstants.Permissions.ApplicationsWrite), Route("api/event-hooks")]
public sealed class EventHooksController : ControllerBase
{
    private readonly IEventHookService _hooks; public EventHooksController(IEventHookService hooks) => _hooks = hooks;
    [HttpPost] public async Task<IActionResult> Create(CreateEventHookRequest request, CancellationToken ct) { var r = await _hooks.CreateAsync(request, ct); return r.IsSuccess ? Created($"/api/event-hooks/{r.Data!.Id}", ApiResponse<object>.Ok(r.Data)) : BadRequest(ApiResponse<object>.Fail(r.ErrorCode, r.Message)); }
    [HttpPost("{id:guid}/verify")] public async Task<IActionResult> Verify(Guid id, CancellationToken ct) => Map(await _hooks.VerifyAsync(id, ct));
    [HttpDelete("{id:guid}")] public async Task<IActionResult> Delete(Guid id, CancellationToken ct) => Map(await _hooks.DeleteAsync(id, ct));
    [HttpPost("deliveries/{id:guid}/replay")] public async Task<IActionResult> Replay(Guid id, CancellationToken ct) => Map(await _hooks.ReplayDeadLetterAsync(id, ct));
    [HttpGet("deliveries")] public async Task<IActionResult> Deliveries([FromQuery] bool deadLettersOnly, CancellationToken ct) => Ok(ApiResponse<object>.Ok(await _hooks.GetDeliveriesAsync(deadLettersOnly, ct)));
    private IActionResult Map(AuthCenter.Application.Common.OperationResult r) => r.IsSuccess ? Ok(ApiResponse.Ok()) : BadRequest(ApiResponse.Fail(r.ErrorCode, r.Message));
}
