using AuthCenter.Application.Common;
using AuthCenter.Application.Models;
using AuthCenter.Contracts.Requests.Governance;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Responses.Governance;

namespace AuthCenter.Application.Interfaces;

/// <summary>Periodic reviews (certifications) of who has access to an application.</summary>
public interface IAccessReviewService
{
    Task<PagedResult<AccessReviewCampaignDto>> GetCampaignsAsync(AccessReviewQuery query, CancellationToken ct = default);
    Task<AccessReviewCampaignDto?> GetCampaignAsync(Guid campaignId, CancellationToken ct = default);
    /// <summary>The active campaigns of the applications the user owns (account portal).</summary>
    Task<IReadOnlyList<AccessReviewCampaignDto>> GetActiveCampaignsForOwnerAsync(Guid ownerUserId, CancellationToken ct = default);
    Task<OperationResult<AccessReviewCampaignDto>> CreateCampaignAsync(CreateAccessReviewRequest request, Guid? createdByUserId, CancellationToken ct = default);
    Task<OperationResult<AccessReviewCampaignDto>> CancelCampaignAsync(Guid campaignId, CancellationToken ct = default);
    Task<OperationResult<PagedResult<AccessReviewItemDto>>> GetItemsAsync(Guid campaignId, AccessReviewItemQuery query, GovernanceActor actor, CancellationToken ct = default);
    Task<OperationResult<AccessReviewItemDto>> DecideAsync(Guid campaignId, Guid itemId, DecideAccessReviewItemRequest request, GovernanceActor actor, CancellationToken ct = default);

    /// <summary>Completes the campaigns past their due date and starts the recurring ones that are due; returns how many.</summary>
    Task<int> RunDueWorkAsync(CancellationToken ct = default);
}
