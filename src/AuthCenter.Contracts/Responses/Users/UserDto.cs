namespace AuthCenter.Contracts.Responses.Users;

public class UserDto
{
    public Guid Id { get; init; }
    public string FullName { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public string? PictureUrl { get; init; }
    public bool IsActive { get; init; }
    public bool IsExternalUser { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? LastLoginAt { get; init; }
    public IReadOnlyList<string> Roles { get; init; } = [];
    public IReadOnlyList<string> Applications { get; init; } = [];
}
