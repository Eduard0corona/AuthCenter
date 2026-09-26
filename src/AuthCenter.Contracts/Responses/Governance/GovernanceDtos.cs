namespace AuthCenter.Contracts.Responses.Governance;

public sealed class GovernanceUserDto
{
    public Guid Id { get; init; }
    public string FullName { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public bool IsActive { get; init; }
}

public sealed class ApplicationGovernanceDto
{
    public Guid ApplicationSystemId { get; init; }
    public string ApplicationCode { get; init; } = string.Empty;
    public string ApplicationName { get; init; } = string.Empty;
    public bool AccessRequestsEnabled { get; init; }
    public IReadOnlyList<GovernanceUserDto> Owners { get; init; } = [];
    /// <summary>0 until the settings are first saved.</summary>
    public long Version { get; init; }
}

public sealed class AccessRequestDto
{
    public Guid Id { get; init; }
    public GovernanceUserDto Requester { get; init; } = new();
    public Guid ApplicationSystemId { get; init; }
    public string ApplicationCode { get; init; } = string.Empty;
    public string ApplicationName { get; init; } = string.Empty;
    public Guid? RoleId { get; init; }
    public string? RoleName { get; init; }
    /// <summary>Portal, Registration or Administrator.</summary>
    public string Source { get; init; } = string.Empty;
    /// <summary>Pending, Approved, Rejected, Cancelled or Expired.</summary>
    public string Status { get; init; } = string.Empty;
    public string? Justification { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? ExpiresAt { get; init; }
    public DateTime? DecidedAt { get; init; }
    public GovernanceUserDto? DecidedBy { get; init; }
    public string? DecisionComment { get; init; }
    public long Version { get; init; }
}

/// <summary>An application the signed-in user may request from the portal.</summary>
public sealed class RequestableApplicationDto
{
    public Guid Id { get; init; }
    public string Code { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public IReadOnlyList<RequestableRoleDto> Roles { get; init; } = [];
    /// <summary>The user already has access (a role may still be requested).</summary>
    public bool HasAccess { get; init; }
}

public sealed class RequestableRoleDto
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
}

public sealed class SeparationOfDutiesRoleDto
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public Guid? ApplicationSystemId { get; init; }
    public string? ApplicationCode { get; init; }
    public bool IsActive { get; init; }
}

public sealed class SeparationOfDutiesRuleDto
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public SeparationOfDutiesRoleDto FirstRole { get; init; } = new();
    public SeparationOfDutiesRoleDto SecondRole { get; init; } = new();
    public bool IsActive { get; init; }
    /// <summary>Users who hold both roles now.</summary>
    public int ViolationCount { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? UpdatedAt { get; init; }
    public long Version { get; init; }
}

public sealed class SeparationOfDutiesViolationDto
{
    public Guid RuleId { get; init; }
    public string RuleName { get; init; } = string.Empty;
    public GovernanceUserDto User { get; init; } = new();
    public SeparationOfDutiesHoldingDto FirstRole { get; init; } = new();
    public SeparationOfDutiesHoldingDto SecondRole { get; init; } = new();
}

/// <summary>A role a user holds, and how: directly and/or through groups.</summary>
public sealed class SeparationOfDutiesHoldingDto
{
    public Guid RoleId { get; init; }
    public string RoleName { get; init; } = string.Empty;
    public string? ApplicationCode { get; init; }
    public bool Direct { get; init; }
    public IReadOnlyList<string> Groups { get; init; } = [];
}

public sealed class AccessReviewCampaignDto
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public Guid ApplicationSystemId { get; init; }
    public string ApplicationCode { get; init; } = string.Empty;
    public string ApplicationName { get; init; } = string.Empty;
    /// <summary>Active, Completed or Cancelled.</summary>
    public string Status { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
    public DateTime DueAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    public bool RevokeUnreviewed { get; init; }
    public int? RecurrenceMonths { get; init; }
    public Guid? PreviousCampaignId { get; init; }
    public int TotalItems { get; init; }
    public int PendingItems { get; init; }
    public int KeptItems { get; init; }
    public int RevokedItems { get; init; }
    public int RemediationItems { get; init; }
    public IReadOnlyList<GovernanceUserDto> Reviewers { get; init; } = [];
    public long Version { get; init; }
}

public sealed class AccessReviewItemDto
{
    public Guid Id { get; init; }
    public Guid CampaignId { get; init; }
    public GovernanceUserDto User { get; init; } = new();
    public bool HasDirectAccess { get; init; }
    public IReadOnlyList<string> Groups { get; init; } = [];
    public IReadOnlyList<string> Roles { get; init; } = [];
    /// <summary>Pending, Keep or Revoke.</summary>
    public string Decision { get; init; } = string.Empty;
    public DateTime? DecidedAt { get; init; }
    public GovernanceUserDto? DecidedBy { get; init; }
    public bool DecidedAutomatically { get; init; }
    public string? Comment { get; init; }
    public string? Outcome { get; init; }
    public bool RemediationRequired { get; init; }
    /// <summary>The caller may decide it now (pending, active campaign, not their own access).</summary>
    public bool CanDecide { get; init; }
    public long Version { get; init; }
}

/// <summary>What waits for the signed-in user as an application owner (account portal).</summary>
public sealed class OwnerApprovalsDto
{
    public IReadOnlyList<AccessRequestDto> Requests { get; init; } = [];
    public IReadOnlyList<AccessReviewCampaignDto> Reviews { get; init; } = [];
}
