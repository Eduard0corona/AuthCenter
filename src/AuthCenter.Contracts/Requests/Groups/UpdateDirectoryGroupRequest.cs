namespace AuthCenter.Contracts.Requests.Groups;

public class UpdateDirectoryGroupRequest
{
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
}
