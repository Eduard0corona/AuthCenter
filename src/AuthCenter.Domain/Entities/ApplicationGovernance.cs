using AuthCenter.Domain.Common;

namespace AuthCenter.Domain.Entities;

/// <summary>
/// Governance settings of an application: whether users may request access to it from the portal.
/// Its owners (<see cref="ApplicationOwner"/>) approve those requests and review who has access.
/// </summary>
public class ApplicationGovernance : IVersionedEntity
{
    public long Version { get; set; }

    public Guid ApplicationSystemId { get; set; }
    public bool AccessRequestsEnabled { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    public ApplicationSystem ApplicationSystem { get; set; } = null!;
}
