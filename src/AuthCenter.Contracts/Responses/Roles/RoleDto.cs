namespace AuthCenter.Contracts.Responses.Roles;

public class RoleDto
{
    /// <summary>Send it back when updating: an update of an older version is rejected with 409.</summary>
    public long Version { get; init; }

    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public Guid? ApplicationSystemId { get; init; }
    public bool IsSystemRole { get; init; }
    public bool IsActive { get; init; }
    public DateTime CreatedAt { get; init; }
    public IReadOnlyList<string> Permissions { get; init; } = [];
}
