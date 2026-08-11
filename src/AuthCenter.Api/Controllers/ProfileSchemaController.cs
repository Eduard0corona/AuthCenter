using AuthCenter.Application.Common;
using AuthCenter.Application.Interfaces;
using AuthCenter.Contracts.Requests.Profiles;
using AuthCenter.Contracts.Responses;
using AuthCenter.Domain.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuthCenter.Api.Controllers;

[ApiController]
[Route("api/profile-schema")]
[Authorize]
public sealed class ProfileSchemaController : ControllerBase
{
    private readonly IUserProfileService _profiles;

    public ProfileSchemaController(IUserProfileService profiles)
    {
        _profiles = profiles;
    }

    [HttpGet]
    [Authorize(Policy = DomainConstants.Permissions.ProfileSchemasRead)]
    public async Task<IActionResult> Get([FromQuery] bool includeInactive, CancellationToken ct) =>
        Ok(ApiResponse<object>.Ok(await _profiles.GetSchemaAsync(includeInactive, ct)));

    [HttpPost]
    [Authorize(Policy = DomainConstants.Permissions.ProfileSchemasWrite)]
    public async Task<IActionResult> Create(CreateProfileAttributeDefinitionRequest request, CancellationToken ct)
    {
        var result = await _profiles.CreateDefinitionAsync(request, ct);
        return result.IsSuccess
            ? Created($"/api/profile-schema/{result.Data!.Id}", ApiResponse<object>.Ok(result.Data))
            : BadRequest(ApiResponse<object>.Fail(result.ErrorCode, result.Message));
    }

    [HttpPut("{definitionId:guid}")]
    [Authorize(Policy = DomainConstants.Permissions.ProfileSchemasWrite)]
    public async Task<IActionResult> Update(
        Guid definitionId,
        UpdateProfileAttributeDefinitionRequest request,
        CancellationToken ct)
    {
        var result = await _profiles.UpdateDefinitionAsync(definitionId, request, ct);
        return result.IsSuccess
            ? Ok(ApiResponse<object>.Ok(result.Data!))
            : BadRequest(ApiResponse<object>.Fail(result.ErrorCode, result.Message));
    }

    [HttpDelete("{definitionId:guid}")]
    [Authorize(Policy = DomainConstants.Permissions.ProfileSchemasWrite)]
    public async Task<IActionResult> Deactivate(Guid definitionId, CancellationToken ct) =>
        ToActionResult(await _profiles.DeactivateDefinitionAsync(definitionId, ct));

    private IActionResult ToActionResult(OperationResult result) =>
        result.IsSuccess
            ? Ok(ApiResponse.Ok())
            : BadRequest(ApiResponse.Fail(result.ErrorCode, result.Message));
}
