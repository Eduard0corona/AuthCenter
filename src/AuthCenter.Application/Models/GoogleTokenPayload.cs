namespace AuthCenter.Application.Models;

public class GoogleTokenPayload
{
    public string Subject { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public bool EmailVerified { get; init; }
    public string? Name { get; init; }
    public string? PictureUrl { get; init; }
}
