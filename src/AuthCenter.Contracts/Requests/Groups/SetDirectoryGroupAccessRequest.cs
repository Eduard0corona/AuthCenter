namespace AuthCenter.Contracts.Requests.Groups;

public sealed class SetDirectoryGroupAccessRequest
{
    public IReadOnlyCollection<Guid> ApplicationSystemIds { get; init; } = [];
    public IReadOnlyCollection<Guid> RoleIds { get; init; } = [];
}
