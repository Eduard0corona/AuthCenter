using AuthCenter.Application.Interfaces;
using AuthCenter.Contracts.Requests.Lifecycle;
using AuthCenter.Contracts.Responses;
using AuthCenter.Domain.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace AuthCenter.Api.Controllers;

[ApiController, Authorize(Policy = DomainConstants.Permissions.UsersWrite), Route("api/lifecycle")]
public sealed class LifecycleAutomationController : ControllerBase
{
    private readonly ILifecycleAutomationService _service; public LifecycleAutomationController(ILifecycleAutomationService service) => _service = service;
    [HttpPost("profile-mappings")] public async Task<IActionResult> CreateMapping(CreateProfileMappingRequest request, CancellationToken ct) => Map(await _service.CreateProfileMappingAsync(request, ct));
    [HttpDelete("profile-mappings/{id:guid}")] public async Task<IActionResult> DeleteMapping(Guid id, CancellationToken ct) => Map(await _service.DeleteProfileMappingAsync(id, ct));
    [HttpPost("group-rules")] public async Task<IActionResult> CreateRule(CreateDynamicGroupRuleRequest request, CancellationToken ct) => Map(await _service.CreateDynamicGroupRuleAsync(request, ct));
    [HttpDelete("group-rules/{id:guid}")] public async Task<IActionResult> DeleteRule(Guid id, CancellationToken ct) => Map(await _service.DeleteDynamicGroupRuleAsync(id, ct));
    private IActionResult Map(AuthCenter.Application.Common.OperationResult result) => result.IsSuccess ? Ok(ApiResponse.Ok()) : BadRequest(ApiResponse.Fail(result.ErrorCode, result.Message));
}
