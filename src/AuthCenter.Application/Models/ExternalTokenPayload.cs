namespace AuthCenter.Application.Models;

public class ExternalTokenPayload
{
    public string Subject { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public string? Name { get; init; }
    public string? PictureUrl { get; init; }

    /// <summary>The provider vouches that the user controls <see cref="Email"/>.</summary>
    public bool EmailVerified { get; init; }
}
