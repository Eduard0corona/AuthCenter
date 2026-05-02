namespace AuthCenter.Contracts.Requests.Roles;

public class CreateRoleRequest
{
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public Guid? ApplicationSystemId { get; init; }
    public bool IsSystemRole { get; init; }
}
