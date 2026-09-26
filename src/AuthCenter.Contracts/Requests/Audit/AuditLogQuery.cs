using AuthCenter.Contracts.Requests.Common;

namespace AuthCenter.Contracts.Requests.Audit;

public class AuditLogQuery : PaginationQuery
{
    public Guid? UserId { get; init; }
    public string? ApplicationCode { get; init; }
    public string? Action { get; init; }
    public string? TraceId { get; init; }

    /// <summary>Events about one kind of entity (for example <c>ApplicationUser</c>, <c>OAuthClient</c>).</summary>
    public string? EntityName { get; init; }

    /// <summary>Events about one entity; combine with <see cref="EntityName"/>.</summary>
    public string? EntityId { get; init; }
    public DateTime? FromUtc { get; init; }
    public DateTime? ToUtc { get; init; }
}
