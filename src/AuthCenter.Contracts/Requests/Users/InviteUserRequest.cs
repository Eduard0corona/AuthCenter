namespace AuthCenter.Contracts.Requests.Users;

public class InviteUserRequest
{
    public string FullName { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public Guid ApplicationSystemId { get; init; }
    public bool GrantActiveAccess { get; init; } = true;
    public IReadOnlyList<Guid> RoleIds { get; init; } = [];
    public string? CallbackBaseUrl { get; init; }
}
