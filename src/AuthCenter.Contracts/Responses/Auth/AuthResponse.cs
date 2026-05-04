namespace AuthCenter.Contracts.Responses.Auth;

public class AuthResponse
{
    public string AccessToken { get; init; } = string.Empty;
    public string RefreshToken { get; init; } = string.Empty;
    public int ExpiresIn { get; init; }
    public AuthenticatedUserDto User { get; init; } = null!;
    public string? DeviceToken { get; init; }
}
