namespace AuthCenter.Contracts.Requests.Groups;

public class CreateDirectoryGroupRequest
{
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
}
