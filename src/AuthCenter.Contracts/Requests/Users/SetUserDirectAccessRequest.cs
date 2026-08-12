namespace AuthCenter.Contracts.Requests.Users;

public sealed class SetUserDirectAccessRequest
{
    public IReadOnlyCollection<Guid> ApplicationSystemIds { get; init; } = [];
    public IReadOnlyCollection<Guid> RoleIds { get; init; } = [];
}
