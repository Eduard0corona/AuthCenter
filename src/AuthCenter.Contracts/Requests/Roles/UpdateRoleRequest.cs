namespace AuthCenter.Contracts.Requests.Roles;

public class UpdateRoleRequest
{
    /// <summary>The version the caller loaded; when sent, a record changed since then is not overwritten (409).</summary>
    public long? Version { get; init; }

    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
}
