namespace AuthCenter.Contracts.Requests.Permissions;

public class UpdatePermissionRequest
{
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
}
