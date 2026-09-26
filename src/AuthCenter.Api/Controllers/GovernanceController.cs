using AuthCenter.Api.Filters;
using AuthCenter.Application.Common;
using AuthCenter.Application.Interfaces;
using AuthCenter.Application.Models;
using AuthCenter.Contracts.Requests.Governance;
using AuthCenter.Contracts.Responses;
using AuthCenter.Domain.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuthCenter.Api.Controllers;

/// <summary>
/// Access governance for administrators: application owners, the access requests queue, access
/// reviews and separation of duties. Owners decide from the portal (<see cref="AccountGovernanceController"/>).
/// </summary>
[ApiController]
[Authorize]
[Route("api/governance")]
public sealed class GovernanceController(
    IAccessGovernanceService governance,
    IAccessReviewService reviews,
    ISeparationOfDutiesService separationOfDuties,
    ICurrentUserService currentUser,
    IAuthorizationService authorization) : ControllerBase
{
    [HttpGet("applications/{id:guid}")]
    [Authorize(Policy = DomainConstants.Permissions.GovernanceRead)]
    public async Task<IActionResult> GetApplication(Guid id, CancellationToken ct) =>
        await governance.GetApplicationAsync(id, ct) is { } application
            ? Ok(ApiResponse<object>.Ok(application))
            : NotFound(ApiResponse<object>.Fail("APP_NOT_FOUND", "Application not found."));

    [HttpPut("applications/{id:guid}")]
    [Authorize(Policy = DomainConstants.Permissions.GovernanceWrite)]
    public async Task<IActionResult> UpdateApplication(Guid id, UpdateApplicationGovernanceRequest request, CancellationToken ct) =>
        Result(await governance.UpdateApplicationAsync(id, request, ct));

    [HttpGet("access-requests")]
    [Authorize(Policy = DomainConstants.Permissions.GovernanceRead)]
    public async Task<IActionResult> GetRequests([FromQuery] AccessRequestQuery query, CancellationToken ct) =>
        Ok(ApiResponse<object>.Ok(await governance.GetRequestsAsync(query, ct)));

    [HttpGet("access-requests/{id:guid}")]
    [Authorize(Policy = DomainConstants.Permissions.GovernanceRead)]
    public async Task<IActionResult> GetRequest(Guid id, CancellationToken ct) =>
        await governance.GetRequestAsync(id, ct) is { } request
            ? Ok(ApiResponse<object>.Ok(request))
            : NotFound(ApiResponse<object>.Fail("ACCESS_REQUEST_NOT_FOUND", "Access request not found."));

    [HttpPost("access-requests/{id:guid}/approve")]
    [Authorize(Policy = DomainConstants.Permissions.GovernanceWrite)]
    public async Task<IActionResult> Approve(Guid id, DecideAccessRequestRequest request, CancellationToken ct) =>
        Actor() is { } actor ? Result(await governance.ApproveAsync(id, actor, request.Comment, ct)) : Unauthorized();

    [HttpPost("access-requests/{id:guid}/reject")]
    [Authorize(Policy = DomainConstants.Permissions.GovernanceWrite)]
    public async Task<IActionResult> Reject(Guid id, DecideAccessRequestRequest request, CancellationToken ct) =>
        Actor() is { } actor ? Result(await governance.RejectAsync(id, actor, request.Comment, ct)) : Unauthorized();

    [HttpGet("access-reviews")]
    [Authorize(Policy = DomainConstants.Permissions.GovernanceRead)]
    public async Task<IActionResult> GetReviews([FromQuery] AccessReviewQuery query, CancellationToken ct) =>
        Ok(ApiResponse<object>.Ok(await reviews.GetCampaignsAsync(query, ct)));

    [HttpGet("access-reviews/{id:guid}")]
    [Authorize(Policy = DomainConstants.Permissions.GovernanceRead)]
    public async Task<IActionResult> GetReview(Guid id, CancellationToken ct) =>
        await reviews.GetCampaignAsync(id, ct) is { } campaign
            ? Ok(ApiResponse<object>.Ok(campaign))
            : NotFound(ApiResponse<object>.Fail("ACCESS_REVIEW_NOT_FOUND", "Access review not found."));

    [HttpPost("access-reviews")]
    [Idempotent]
    [Authorize(Policy = DomainConstants.Permissions.GovernanceWrite)]
    public async Task<IActionResult> CreateReview(CreateAccessReviewRequest request, CancellationToken ct)
    {
        var result = await reviews.CreateCampaignAsync(request, currentUser.UserId, ct);
        return result.IsSuccess
            ? CreatedAtAction(nameof(GetReview), new { id = result.Data!.Id }, ApiResponse<object>.Ok(result.Data))
            : Result(result);
    }

    [HttpPost("access-reviews/{id:guid}/cancel")]
    [Authorize(Policy = DomainConstants.Permissions.GovernanceWrite)]
    public async Task<IActionResult> CancelReview(Guid id, CancellationToken ct) =>
        Result(await reviews.CancelCampaignAsync(id, ct));

    /// <summary>Governance readers see the items; deciding needs the write permission.</summary>
    [HttpGet("access-reviews/{id:guid}/items")]
    [Authorize(Policy = DomainConstants.Permissions.GovernanceRead)]
    public async Task<IActionResult> GetReviewItems(Guid id, [FromQuery] AccessReviewItemQuery query, CancellationToken ct)
    {
        if (currentUser.UserId is not { } userId)
            return Unauthorized();
        var canDecide = (await authorization.AuthorizeAsync(User, DomainConstants.Permissions.GovernanceWrite)).Succeeded;
        return Result(await reviews.GetItemsAsync(id, query, new GovernanceActor(userId, IsAdministrator: true, CanDecide: canDecide), ct));
    }

    [HttpPost("access-reviews/{id:guid}/items/{itemId:guid}/decision")]
    [Authorize(Policy = DomainConstants.Permissions.GovernanceWrite)]
    public async Task<IActionResult> DecideReviewItem(Guid id, Guid itemId, DecideAccessReviewItemRequest request, CancellationToken ct) =>
        Actor() is { } actor ? Result(await reviews.DecideAsync(id, itemId, request, actor, ct)) : Unauthorized();

    [HttpGet("sod-rules")]
    [Authorize(Policy = DomainConstants.Permissions.GovernanceRead)]
    public async Task<IActionResult> GetRules([FromQuery] SeparationOfDutiesRuleQuery query, CancellationToken ct) =>
        Ok(ApiResponse<object>.Ok(await separationOfDuties.GetRulesAsync(query, ct)));

    [HttpGet("sod-rules/{id:guid}")]
    [Authorize(Policy = DomainConstants.Permissions.GovernanceRead)]
    public async Task<IActionResult> GetRule(Guid id, CancellationToken ct) =>
        await separationOfDuties.GetRuleAsync(id, ct) is { } rule
            ? Ok(ApiResponse<object>.Ok(rule))
            : NotFound(ApiResponse<object>.Fail("SOD_RULE_NOT_FOUND", "Separation of duties rule not found."));

    [HttpPost("sod-rules")]
    [Idempotent]
    [Authorize(Policy = DomainConstants.Permissions.GovernanceWrite)]
    public async Task<IActionResult> CreateRule(SeparationOfDutiesRuleRequest request, CancellationToken ct)
    {
        var result = await separationOfDuties.CreateRuleAsync(request, ct);
        return result.IsSuccess
            ? CreatedAtAction(nameof(GetRule), new { id = result.Data!.Id }, ApiResponse<object>.Ok(result.Data))
            : Result(result);
    }

    [HttpPut("sod-rules/{id:guid}")]
    [Authorize(Policy = DomainConstants.Permissions.GovernanceWrite)]
    public async Task<IActionResult> UpdateRule(Guid id, SeparationOfDutiesRuleRequest request, CancellationToken ct) =>
        Result(await separationOfDuties.UpdateRuleAsync(id, request, ct));

    [HttpDelete("sod-rules/{id:guid}")]
    [Authorize(Policy = DomainConstants.Permissions.GovernanceWrite)]
    public async Task<IActionResult> DeleteRule(Guid id, CancellationToken ct)
    {
        var result = await separationOfDuties.DeleteRuleAsync(id, ct);
        return result.IsSuccess ? Ok(ApiResponse.Ok()) : Status(result.ErrorCode, ApiResponse.Fail(result.ErrorCode, result.Message));
    }

    [HttpGet("sod-violations")]
    [Authorize(Policy = DomainConstants.Permissions.GovernanceRead)]
    public async Task<IActionResult> GetViolations([FromQuery] SeparationOfDutiesViolationQuery query, CancellationToken ct) =>
        Ok(ApiResponse<object>.Ok(await separationOfDuties.GetViolationsAsync(query, ct)));

    private GovernanceActor? Actor() => currentUser.UserId is { } userId ? new GovernanceActor(userId, IsAdministrator: true) : null;

    private IActionResult Result<T>(OperationResult<T> result) =>
        result.IsSuccess ? Ok(ApiResponse<object>.Ok(result.Data!)) : Status(result.ErrorCode, ApiResponse<object>.Fail(result.ErrorCode, result.Message, result.Details));

    /// <summary>Not found is 404, a decision that is not the caller's to take 403; anything else 400 (409 for stale versions).</summary>
    internal ObjectResult Status(string errorCode, object body) => errorCode switch
    {
        _ when errorCode.EndsWith("_NOT_FOUND", StringComparison.Ordinal) => NotFound(body),
        "ACCESS_REQUEST_FORBIDDEN" or "ACCESS_REVIEW_FORBIDDEN" or "SELF_APPROVAL_FORBIDDEN" or "SELF_REVIEW_FORBIDDEN" => StatusCode(StatusCodes.Status403Forbidden, body),
        "ACCESS_REQUEST_NOT_PENDING" or "ACCESS_REVIEW_ITEM_DECIDED" or "ACCESS_REVIEW_ACTIVE_EXISTS" or "SOD_RULE_EXISTS" or "SOD_CONFLICT" => Conflict(body),
        _ => BadRequest(body)
    };
}
