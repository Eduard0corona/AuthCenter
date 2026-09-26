using AuthCenter.Domain.Common;
using AuthCenter.Domain.Enums;

namespace AuthCenter.Domain.Entities;

/// <summary>
/// A request for access to an application (optionally with one of its roles), decided by an owner
/// of the application or a governance administrator, never by the requester.
/// </summary>
public class AccessRequest : IVersionedEntity
{
    public long Version { get; set; }

    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid ApplicationSystemId { get; set; }
    public Guid? RequestedRoleId { get; set; }
    public AccessRequestSource Source { get; set; }
    public AccessRequestStatus Status { get; set; }
    public string? Justification { get; set; }
    public DateTime CreatedAt { get; set; }
    /// <summary>A pending request past this instant expires; requests from a registration do not.</summary>
    public DateTime? ExpiresAt { get; set; }
    public DateTime? DecidedAt { get; set; }
    /// <summary>Who decided; null when the request expired or the decision was not a person's.</summary>
    public Guid? DecidedByUserId { get; set; }
    public string? DecisionComment { get; set; }

    public ApplicationUser User { get; set; } = null!;
    public ApplicationSystem ApplicationSystem { get; set; } = null!;
    public ApplicationRole? RequestedRole { get; set; }
}
