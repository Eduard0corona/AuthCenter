namespace AuthCenter.Contracts.Requests.Users;

public class CreateUserRequest
{
    public string FullName { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public string? Password { get; init; }
    public bool IsTemporaryPassword { get; init; }
    public Guid? ApplicationSystemId { get; init; }
    public bool GrantApplicationAccess { get; init; }
    public bool ApplicationAccessIsActive { get; init; } = true;
    public IReadOnlyList<Guid> RoleIds { get; init; } = [];
}
