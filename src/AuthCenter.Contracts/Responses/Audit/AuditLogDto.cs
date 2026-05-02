namespace AuthCenter.Contracts.Responses.Audit;

public class AuditLogDto
{
    public Guid Id { get; init; }
    public Guid? UserId { get; init; }
    public string? ApplicationCode { get; init; }
    public string Action { get; init; } = string.Empty;
    public string? EntityName { get; init; }
    public string? EntityId { get; init; }
    public string? IpAddress { get; init; }
    public string? UserAgent { get; init; }
    public string? MetadataJson { get; init; }
    public DateTime CreatedAt { get; init; }
}
