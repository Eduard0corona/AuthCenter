namespace AuthCenter.Contracts.Responses.Users;

public class UserDto
{
    /// <summary>Send it back when updating: an update of an older version is rejected with 409.</summary>
    public long Version { get; init; }

    public Guid Id { get; init; }
    public string FullName { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public string? PictureUrl { get; init; }
    public bool IsActive { get; init; }
    public bool IsExternalUser { get; init; }
    public bool HasLocalPassword { get; init; }
    public bool MustChangePassword { get; init; }
    public bool MfaEnabled { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? LastLoginAt { get; init; }
    public IReadOnlyList<string> Roles { get; init; } = [];
    public IReadOnlyList<string> Applications { get; init; } = [];
    public IReadOnlyList<UserApplicationAccessDto> ApplicationAccesses { get; init; } = [];
    public IReadOnlyList<UserApplicationAssignmentDto> ApplicationAssignments { get; init; } = [];
    public IReadOnlyList<UserRoleAssignmentDto> RoleAssignments { get; init; } = [];
    public IReadOnlyList<UserGroupMembershipDto> GroupMemberships { get; init; } = [];
}

public class UserApplicationAssignmentDto
{
    public Guid ApplicationId { get; init; }
    public string ApplicationCode { get; init; } = string.Empty;
    public string ApplicationName { get; init; } = string.Empty;
    public bool IsApplicationActive { get; init; }
    public bool IsDirect { get; init; }
    public string? DirectAccessStatus { get; init; }
    public bool IsEffective { get; init; }
    public IReadOnlyList<UserInheritedAccessSourceDto> InheritedFromGroups { get; init; } = [];
}

public class UserRoleAssignmentDto
{
    public Guid RoleId { get; init; }
    public string RoleName { get; init; } = string.Empty;
    public Guid? ApplicationId { get; init; }
    public string? ApplicationCode { get; init; }
    public bool IsRoleActive { get; init; }
    public bool IsSystemRole { get; init; }
    public bool IsDirect { get; init; }
    public bool IsEffective { get; init; }
    public IReadOnlyList<UserInheritedAccessSourceDto> InheritedFromGroups { get; init; } = [];
}

public class UserGroupMembershipDto
{
    public Guid GroupId { get; init; }
    public string GroupName { get; init; } = string.Empty;
    public bool IsGroupActive { get; init; }
    public DateTime AddedAt { get; init; }
}

public class UserInheritedAccessSourceDto
{
    public Guid GroupId { get; init; }
    public string GroupName { get; init; } = string.Empty;
    public bool IsActive { get; init; }
}

public class UserApplicationAccessDto
{
    public Guid ApplicationId { get; init; }
    public string ApplicationCode { get; init; } = string.Empty;
    public string ApplicationName { get; init; } = string.Empty;
    public bool IsActive { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? RevokedAt { get; init; }
}
