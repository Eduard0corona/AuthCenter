namespace AuthCenter.Contracts.Requests.Users;

public class UpdateUserRequest
{
    public string FullName { get; init; } = string.Empty;
    public string? PictureUrl { get; init; }
}
