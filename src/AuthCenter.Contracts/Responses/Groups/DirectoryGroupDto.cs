namespace AuthCenter.Contracts.Responses.Groups;

public class DirectoryGroupDto
{
    /// <summary>Send it back when updating: an update of an older version is rejected with 409.</summary>
    public long Version { get; init; }

    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public bool IsActive { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? UpdatedAt { get; init; }
    public int MemberCount { get; init; }
    public IReadOnlyList<DirectoryGroupApplicationDto> Applications { get; init; } = [];
    public IReadOnlyList<DirectoryGroupRoleDto> Roles { get; init; } = [];
}

public class DirectoryGroupApplicationDto
{
    public Guid Id { get; init; }
    public string Code { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
}

public class DirectoryGroupRoleDto
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public Guid ApplicationSystemId { get; init; }
    public string ApplicationCode { get; init; } = string.Empty;
}

public class DirectoryGroupMemberDto
{
    public Guid UserId { get; init; }
    public string FullName { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public bool IsActive { get; init; }
    public DateTime AddedAt { get; init; }
}
