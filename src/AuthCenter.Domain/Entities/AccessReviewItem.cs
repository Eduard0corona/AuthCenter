using AuthCenter.Domain.Common;
using AuthCenter.Domain.Enums;

namespace AuthCenter.Domain.Entities;

/// <summary>One user's access to the campaign's application, as it was when the campaign started.</summary>
public class AccessReviewItem : IVersionedEntity
{
    public long Version { get; set; }

    public Guid Id { get; set; }
    public Guid CampaignId { get; set; }
    public Guid UserId { get; set; }
    /// <summary>The user had direct access (which a revocation removes).</summary>
    public bool HasDirectAccess { get; set; }
    /// <summary>Groups that granted the access, comma separated (a revocation cannot remove those).</summary>
    public string? GroupNames { get; set; }
    /// <summary>The user's roles in the application, comma separated.</summary>
    public string? RoleNames { get; set; }
    public AccessReviewDecision Decision { get; set; }
    public DateTime? DecidedAt { get; set; }
    public Guid? DecidedByUserId { get; set; }
    /// <summary>Decided by the due date rather than by a reviewer.</summary>
    public bool DecidedAutomatically { get; set; }
    public string? Comment { get; set; }
    /// <summary>What the decision did, or what is left to do (group memberships to remove).</summary>
    public string? Outcome { get; set; }
    public bool RemediationRequired { get; set; }

    public AccessReviewCampaign Campaign { get; set; } = null!;
    public ApplicationUser User { get; set; } = null!;
}
