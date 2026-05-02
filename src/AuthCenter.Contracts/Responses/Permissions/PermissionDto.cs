namespace AuthCenter.Contracts.Responses.Permissions;

public class PermissionDto
{
    public Guid Id { get; init; }
    public Guid ApplicationSystemId { get; init; }
    public string Code { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public bool IsActive { get; init; }
    public DateTime CreatedAt { get; init; }
}
