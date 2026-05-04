namespace AuthCenter.Contracts.Responses.Auth;

public class TrustedDeviceDto
{
    public Guid Id { get; init; }
    public string? DeviceName { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime ExpiresAt { get; init; }
    public DateTime? LastUsedAt { get; init; }
}
