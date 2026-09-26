using AuthCenter.Contracts.Requests.Common;

namespace AuthCenter.Contracts.Requests.Governance;

/// <summary>The owners of an application and whether users may request access to it from the portal.</summary>
public sealed class UpdateApplicationGovernanceRequest
{
    public bool AccessRequestsEnabled { get; init; }
    public IReadOnlyList<Guid> OwnerUserIds { get; init; } = [];
    /// <summary>The version that was loaded; an older one is refused with 409.</summary>
    public long? Version { get; init; }
}

public sealed class AccessRequestQuery : PaginationQuery
{
    /// <summary>Pending, Approved, Rejected, Cancelled or Expired.</summary>
    public string? Status { get; init; }
    public Guid? ApplicationSystemId { get; init; }
    public Guid? UserId { get; init; }
    /// <summary>Requester name or email.</summary>
    public string? Search { get; init; }
}

public sealed class DecideAccessRequestRequest
{
    /// <summary>Shown to the requester; required to reject.</summary>
    public string? Comment { get; init; }
}

/// <summary>A user's request, from the portal, for access to an application and optionally one of its roles.</summary>
public sealed class CreateAccessRequestRequest
{
    public Guid ApplicationSystemId { get; init; }
    public Guid? RoleId { get; init; }
    public string Justification { get; init; } = string.Empty;
}

public sealed class SeparationOfDutiesRuleRequest
{
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public Guid FirstRoleId { get; init; }
    public Guid SecondRoleId { get; init; }
    public bool IsActive { get; init; } = true;
    /// <summary>On update, the version that was loaded; an older one is refused with 409.</summary>
    public long? Version { get; init; }
}

public sealed class SeparationOfDutiesRuleQuery : PaginationQuery
{
    public string? Search { get; init; }
    public bool? IsActive { get; init; }
}

public sealed class SeparationOfDutiesViolationQuery : PaginationQuery
{
    public Guid? RuleId { get; init; }
}

public sealed class CreateAccessReviewRequest
{
    public string Name { get; init; } = string.Empty;
    public Guid ApplicationSystemId { get; init; }
    /// <summary>UTC; between one day and one year from now.</summary>
    public DateTime DueAt { get; init; }
    /// <summary>At the due date, accesses nobody reviewed are revoked instead of kept.</summary>
    public bool RevokeUnreviewed { get; init; }
    /// <summary>1 to 12: a new campaign starts that many months after this one started.</summary>
    public int? RecurrenceMonths { get; init; }
}

public sealed class AccessReviewQuery : PaginationQuery
{
    /// <summary>Active, Completed or Cancelled.</summary>
    public string? Status { get; init; }
    public Guid? ApplicationSystemId { get; init; }
}

public sealed class AccessReviewItemQuery : PaginationQuery
{
    /// <summary>Pending, Keep or Revoke.</summary>
    public string? Decision { get; init; }
    public bool? RemediationRequired { get; init; }
    /// <summary>Name or email.</summary>
    public string? Search { get; init; }
}

public sealed class DecideAccessReviewItemRequest
{
    /// <summary>Keep or Revoke.</summary>
    public string Decision { get; init; } = string.Empty;
    public string? Comment { get; init; }
}
