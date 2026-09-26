using AuthCenter.Api.Filters;
using AuthCenter.Application.Common;
using AuthCenter.Application.Interfaces;
using AuthCenter.Contracts.Requests.Lifecycle;
using AuthCenter.Contracts.Responses;
using AuthCenter.Domain.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
namespace AuthCenter.Api.Controllers;

[ApiController, Authorize, Route("api/lifecycle")]
public sealed class LifecycleAutomationController : ControllerBase
{
    private readonly ILifecycleAutomationService _service; public LifecycleAutomationController(ILifecycleAutomationService service) => _service = service;
    [HttpGet("profile-mappings"), Authorize(Policy = DomainConstants.Permissions.UsersRead)] public async Task<IActionResult> GetMappings([FromQuery] ProfileMappingQuery query, CancellationToken ct) => Ok(ApiResponse<object>.Ok(await _service.GetProfileMappingsAsync(query, ct)));
    [HttpGet("profile-mappings/{id:guid}"), Authorize(Policy = DomainConstants.Permissions.UsersRead)] public async Task<IActionResult> GetMapping(Guid id, CancellationToken ct) { var item = await _service.GetProfileMappingAsync(id, ct); return item is null ? NotFound(ApiResponse<object>.Fail("PROFILE_MAPPING_NOT_FOUND", "Mapping not found.")) : Ok(ApiResponse<object>.Ok(item)); }
    [HttpPost("profile-mappings"), Idempotent, Authorize(Policy = DomainConstants.Permissions.UsersWrite)] public async Task<IActionResult> CreateMapping(CreateProfileMappingRequest request, CancellationToken ct) => Map(await _service.CreateProfileMappingAsync(request, ct));
    [HttpPost("profile-mappings/validate"), Authorize(Policy = DomainConstants.Permissions.UsersRead)] public async Task<IActionResult> ValidateMapping(CreateProfileMappingRequest request, CancellationToken ct) => Map(await _service.ValidateProfileMappingAsync(request, ct));
    [HttpPut("profile-mappings/{id:guid}"), Authorize(Policy = DomainConstants.Permissions.UsersWrite)] public async Task<IActionResult> UpdateMapping(Guid id, UpdateProfileMappingRequest request, CancellationToken ct) => Map(await _service.UpdateProfileMappingAsync(id, request, ct));
    [HttpPost("profile-mappings/{id:guid}/simulate"), Authorize(Policy = DomainConstants.Permissions.UsersRead)] public async Task<IActionResult> SimulateMapping(Guid id, ProfileMappingSimulationRequest request, CancellationToken ct) => Map(await _service.SimulateProfileMappingAsync(id, request, ct));
    [HttpDelete("profile-mappings/{id:guid}"), Authorize(Policy = DomainConstants.Permissions.UsersWrite)] public async Task<IActionResult> DeleteMapping(Guid id, CancellationToken ct) => Map(await _service.DeleteProfileMappingAsync(id, ct));
    [HttpGet("group-rules"), Authorize(Policy = DomainConstants.Permissions.GroupsRead)] public async Task<IActionResult> GetRules([FromQuery] DynamicGroupRuleQuery query, CancellationToken ct) => Ok(ApiResponse<object>.Ok(await _service.GetDynamicGroupRulesAsync(query, ct)));
    [HttpGet("group-rules/{id:guid}"), Authorize(Policy = DomainConstants.Permissions.GroupsRead)] public async Task<IActionResult> GetRule(Guid id, CancellationToken ct) { var item = await _service.GetDynamicGroupRuleAsync(id, ct); return item is null ? NotFound(ApiResponse<object>.Fail("GROUP_RULE_NOT_FOUND", "Rule not found.")) : Ok(ApiResponse<object>.Ok(item)); }
    [HttpPost("group-rules"), Idempotent, Authorize(Policy = DomainConstants.Permissions.GroupsWrite)] public async Task<IActionResult> CreateRule(CreateDynamicGroupRuleRequest request, CancellationToken ct) => Map(await _service.CreateDynamicGroupRuleAsync(request, ct));
    [HttpPut("group-rules/{id:guid}"), Authorize(Policy = DomainConstants.Permissions.GroupsWrite)] public async Task<IActionResult> UpdateRule(Guid id, UpdateDynamicGroupRuleRequest request, CancellationToken ct) => Map(await _service.UpdateDynamicGroupRuleAsync(id, request, ct));
    [HttpPost("group-rules/{id:guid}/preview"), Authorize(Policy = DomainConstants.Permissions.GroupsRead)] public async Task<IActionResult> PreviewRule(Guid id, GroupRulePreviewRequest request, CancellationToken ct) => Map(await _service.PreviewDynamicGroupRuleAsync(id, request, ct));
    [HttpDelete("group-rules/{id:guid}"), Authorize(Policy = DomainConstants.Permissions.GroupsWrite)] public async Task<IActionResult> DeleteRule(Guid id, CancellationToken ct) => Map(await _service.DeleteDynamicGroupRuleAsync(id, ct));
    private IActionResult Map(OperationResult result) => result.IsSuccess ? Ok(ApiResponse.Ok()) : Failure(result.ErrorCode, result.Message);
    private IActionResult Map<T>(OperationResult<T> result) => result.IsSuccess ? Ok(ApiResponse<object>.Ok(result.Data!)) : Failure(result.ErrorCode, result.Message);
    private IActionResult Failure(string code, string message) => code == "CONCURRENCY_CONFLICT" ? Conflict(ApiResponse.Fail(code, message)) : BadRequest(ApiResponse.Fail(code, message));
}
