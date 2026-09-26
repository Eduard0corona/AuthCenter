namespace AuthCenter.Contracts.Responses.Auth;

/// <summary>An application the signed-in user can use, directly or through a group (account portal).</summary>
public sealed class UserApplicationDto
{
    public string Code { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string? LogoUrl { get; init; }
    public string? SupportUrl { get; init; }

    /// <summary>When direct access was granted; null when the access only comes from a group.</summary>
    public DateTime? GrantedAt { get; init; }
    public IReadOnlyList<string> Groups { get; init; } = [];
}
