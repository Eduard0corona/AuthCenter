namespace AuthCenter.Contracts.Requests.Permissions;

public class CreatePermissionRequest
{
    public Guid ApplicationSystemId { get; init; }
    public string Code { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
}
