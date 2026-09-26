using AuthCenter.Contracts.Requests.Audit;
using AuthCenter.Contracts.Responses;
using AuthCenter.Contracts.Responses.Audit;

namespace AuthCenter.Application.Interfaces;

public interface IAuditService
{
    Task LogAsync(
        string action,
        Guid? userId = null,
        string? applicationCode = null,
        string? entityName = null,
        string? entityId = null,
        string? ipAddress = null,
        string? userAgent = null,
        object? metadata = null,
        CancellationToken ct = default);

    Task<PagedResult<AuditLogDto>> GetAsync(AuditLogQuery query, CancellationToken ct = default);
    /// <summary>The newest <paramref name="maxRows"/> events matching the filters (paging is ignored), audited as an export.</summary>
    Task<AuditLogExport> ExportAsync(AuditLogQuery query, int maxRows, CancellationToken ct = default);
}
