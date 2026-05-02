namespace AuthCenter.Contracts.Requests.Roles;

public class UpdateRoleRequest
{
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
}
