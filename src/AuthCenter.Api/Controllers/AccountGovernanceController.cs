using AuthCenter.Application.Common;
using AuthCenter.Application.Interfaces;
using AuthCenter.Application.Models;
using AuthCenter.Contracts.Requests.Governance;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Responses.Governance;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AuthCenter.Api.Controllers;

/// <summary>
/// Access governance in the account portal: users request access to applications, and application
/// owners decide those requests and review who has access. Owners act only on the applications
/// they own and never on themselves.
/// </summary>
[ApiController]
[Authorize]
[Route("api/auth")]
public sealed class AccountGovernanceController(
    IAccessGovernanceService governance,
    IAccessReviewService reviews,
    ICurrentUserService currentUser) : ControllerBase
{
    /// <summary>Applications the user may request, with their requestable roles.</summary>
    [HttpGet("access-requests/catalog")]
    public async Task<IActionResult> Catalog(CancellationToken ct) =>
        currentUser.UserId is { } userId ? Ok(ApiResponse<object>.Ok(await governance.GetRequestableApplicationsAsync(userId, ct))) : Unauthorized();

    [HttpGet("access-requests")]
    public async Task<IActionResult> Requests(CancellationToken ct) =>
        currentUser.UserId is { } userId ? Ok(ApiResponse<object>.Ok(await governance.GetUserRequestsAsync(userId, ct))) : Unauthorized();

    [HttpPost("access-requests")]
    public async Task<IActionResult> CreateRequest(CreateAccessRequestRequest request, CancellationToken ct) =>
        currentUser.UserId is { } userId ? Result(await governance.CreateRequestAsync(userId, request, ct)) : Unauthorized();

    [HttpPost("access-requests/{id:guid}/cancel")]
    public async Task<IActionResult> CancelRequest(Guid id, CancellationToken ct) =>
        currentUser.UserId is { } userId ? Result(await governance.CancelRequestAsync(userId, id, ct)) : Unauthorized();

    /// <summary>What waits for the user as an owner: requests to decide and reviews in course.</summary>
    [HttpGet("approvals")]
    public async Task<IActionResult> Approvals(CancellationToken ct)
    {
        if (currentUser.UserId is not { } userId)
            return Unauthorized();
        return Ok(ApiResponse<object>.Ok(new OwnerApprovalsDto
        {
            Requests = await governance.GetPendingRequestsForOwnerAsync(userId, ct),
            Reviews = await reviews.GetActiveCampaignsForOwnerAsync(userId, ct)
        }));
    }

    [HttpPost("approvals/requests/{id:guid}/approve")]
    public async Task<IActionResult> Approve(Guid id, DecideAccessRequestRequest request, CancellationToken ct) =>
        Owner() is { } owner ? Result(await governance.ApproveAsync(id, owner, request.Comment, ct)) : Unauthorized();

    [HttpPost("approvals/requests/{id:guid}/reject")]
    public async Task<IActionResult> Reject(Guid id, DecideAccessRequestRequest request, CancellationToken ct) =>
        Owner() is { } owner ? Result(await governance.RejectAsync(id, owner, request.Comment, ct)) : Unauthorized();

    [HttpGet("approvals/reviews/{campaignId:guid}/items")]
    public async Task<IActionResult> ReviewItems(Guid campaignId, [FromQuery] AccessReviewItemQuery query, CancellationToken ct) =>
        Owner() is { } owner ? Result(await reviews.GetItemsAsync(campaignId, query, owner, ct)) : Unauthorized();

    [HttpPost("approvals/reviews/{campaignId:guid}/items/{itemId:guid}")]
    public async Task<IActionResult> DecideReviewItem(Guid campaignId, Guid itemId, DecideAccessReviewItemRequest request, CancellationToken ct) =>
        Owner() is { } owner ? Result(await reviews.DecideAsync(campaignId, itemId, request, owner, ct)) : Unauthorized();

    private GovernanceActor? Owner() => currentUser.UserId is { } userId ? new GovernanceActor(userId, IsAdministrator: false) : null;

    private IActionResult Result<T>(OperationResult<T> result)
    {
        if (result.IsSuccess)
            return Ok(ApiResponse<object>.Ok(result.Data!));
        var body = ApiResponse<object>.Fail(result.ErrorCode, result.Message, result.Details);
        return result.ErrorCode switch
        {
            _ when result.ErrorCode.EndsWith("_NOT_FOUND", StringComparison.Ordinal) => NotFound(body),
            "ACCESS_REQUEST_FORBIDDEN" or "ACCESS_REVIEW_FORBIDDEN" or "SELF_APPROVAL_FORBIDDEN" or "SELF_REVIEW_FORBIDDEN" => StatusCode(StatusCodes.Status403Forbidden, body),
            "ACCESS_REQUEST_NOT_PENDING" or "ACCESS_REQUEST_EXISTS" or "ACCESS_REVIEW_ITEM_DECIDED" or "SOD_CONFLICT" => Conflict(body),
            _ => BadRequest(body)
        };
    }
}
