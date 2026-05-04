namespace AuthCenter.Contracts.Responses.Auth;

public class ExternalProviderDto
{
    public Guid Id { get; init; }
    public string Provider { get; init; } = string.Empty;
    public string? Email { get; init; }
    public string? DisplayName { get; init; }
    public string? PictureUrl { get; init; }
    public DateTime LinkedAt { get; init; }
    public DateTime? LastUsedAt { get; init; }
}
