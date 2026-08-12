namespace AuthCenter.Contracts.Requests.Roles;

public sealed class SetRolePermissionsRequest
{
    public IReadOnlyCollection<Guid> PermissionIds { get; init; } = [];
}
