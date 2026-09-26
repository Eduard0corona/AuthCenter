using AuthCenter.Application.Common;
using AuthCenter.Application.Models;
using AuthCenter.Contracts.Requests.Governance;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Responses.Governance;
using AuthCenter.Domain.Enums;

namespace AuthCenter.Application.Interfaces;

/// <summary>Application owners and access requests with approval.</summary>
public interface IAccessGovernanceService
{
    Task<ApplicationGovernanceDto?> GetApplicationAsync(Guid applicationSystemId, CancellationToken ct = default);
    Task<OperationResult<ApplicationGovernanceDto>> UpdateApplicationAsync(Guid applicationSystemId, UpdateApplicationGovernanceRequest request, CancellationToken ct = default);

    Task<PagedResult<AccessRequestDto>> GetRequestsAsync(AccessRequestQuery query, CancellationToken ct = default);
    Task<AccessRequestDto?> GetRequestAsync(Guid requestId, CancellationToken ct = default);
    Task<OperationResult<AccessRequestDto>> ApproveAsync(Guid requestId, GovernanceActor actor, string? comment, CancellationToken ct = default);
    Task<OperationResult<AccessRequestDto>> RejectAsync(Guid requestId, GovernanceActor actor, string? comment, CancellationToken ct = default);

    /// <summary>Approves a user's pending access to an application (and the request behind it).</summary>
    Task<OperationResult> ApprovePendingAccessAsync(Guid userId, Guid applicationSystemId, GovernanceActor actor, CancellationToken ct = default);

    Task<IReadOnlyList<RequestableApplicationDto>> GetRequestableApplicationsAsync(Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<AccessRequestDto>> GetUserRequestsAsync(Guid userId, CancellationToken ct = default);
    Task<OperationResult<AccessRequestDto>> CreateRequestAsync(Guid userId, CreateAccessRequestRequest request, CancellationToken ct = default);
    Task<OperationResult<AccessRequestDto>> CancelRequestAsync(Guid userId, Guid requestId, CancellationToken ct = default);

    /// <summary>The pending requests of the applications the user owns (other than their own).</summary>
    Task<IReadOnlyList<AccessRequestDto>> GetPendingRequestsForOwnerAsync(Guid ownerUserId, CancellationToken ct = default);

    /// <summary>
    /// Records the request behind access left pending approval (a registration, or an administrator
    /// who created the user that way) unless one is open, and tells the application's owners. The
    /// caller saves it with the pending access.
    /// </summary>
    Task OpenPendingRequestAsync(Guid userId, Guid applicationSystemId, AccessRequestSource source, CancellationToken ct = default);

    /// <summary>Marks pending requests past their expiry as expired; returns how many.</summary>
    Task<int> ExpireRequestsAsync(CancellationToken ct = default);
}
