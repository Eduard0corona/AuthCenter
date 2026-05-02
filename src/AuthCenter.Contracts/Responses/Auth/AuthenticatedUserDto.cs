namespace AuthCenter.Contracts.Responses.Auth;

public class AuthenticatedUserDto
{
    public Guid Id { get; init; }
    public string FullName { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public string? PictureUrl { get; init; }
    public IReadOnlyList<string> Applications { get; init; } = [];
    public IReadOnlyList<string> Roles { get; init; } = [];
    public IReadOnlyList<string> Permissions { get; init; } = [];
}
