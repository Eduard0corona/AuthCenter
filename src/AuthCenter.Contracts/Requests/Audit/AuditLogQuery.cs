using AuthCenter.Contracts.Requests.Common;

namespace AuthCenter.Contracts.Requests.Audit;

public class AuditLogQuery : PaginationQuery
{
    public Guid? UserId { get; init; }
    public string? ApplicationCode { get; init; }
    public string? Action { get; init; }
    public string? TraceId { get; init; }
    public DateTime? FromUtc { get; init; }
    public DateTime? ToUtc { get; init; }
}
