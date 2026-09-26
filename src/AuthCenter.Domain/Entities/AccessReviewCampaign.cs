using AuthCenter.Domain.Common;
using AuthCenter.Domain.Enums;

namespace AuthCenter.Domain.Entities;

/// <summary>
/// A periodic review of who has access to an application: the owners (or governance
/// administrators) keep or revoke each access captured when the campaign started.
/// </summary>
public class AccessReviewCampaign : IVersionedEntity
{
    public long Version { get; set; }

    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public Guid ApplicationSystemId { get; set; }
    public AccessReviewStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public DateTime DueAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    /// <summary>At the due date, accesses nobody reviewed are revoked (otherwise kept).</summary>
    public bool RevokeUnreviewed { get; set; }
    /// <summary>When set, a new campaign starts this many months after this one started.</summary>
    public int? RecurrenceMonths { get; set; }
    /// <summary>The campaign this one repeats, if it was started by a recurrence.</summary>
    public Guid? PreviousCampaignId { get; set; }
    /// <summary>Claimed by an instance while it completes the campaign or starts the next one.</summary>
    public DateTime? LockedUntil { get; set; }
    /// <summary>The next campaign was already started (recurring campaigns only).</summary>
    public bool NextStarted { get; set; }

    public ApplicationSystem ApplicationSystem { get; set; } = null!;
    public ICollection<AccessReviewItem> Items { get; set; } = new List<AccessReviewItem>();
}
