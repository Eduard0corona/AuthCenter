namespace AuthCenter.Contracts.Responses.Audit;

/// <summary>The exported events and how many matched the filters (more than exported when capped).</summary>
public sealed record AuditLogExport(IReadOnlyList<AuditLogDto> Items, int TotalCount);
