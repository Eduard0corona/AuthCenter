namespace AuthCenter.Contracts.Responses.Auth;

public class SessionDto
{
    public Guid Id { get; init; }
    public string ApplicationCode { get; init; } = string.Empty;
    public string? IpAddress { get; init; }
    public string? UserAgent { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime ExpiresAt { get; init; }
}
