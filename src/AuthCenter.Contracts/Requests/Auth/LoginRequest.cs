namespace AuthCenter.Contracts.Requests.Auth;

public class LoginRequest
{
    public string Email { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
    public string ApplicationCode { get; init; } = string.Empty;
    public string? DeviceToken { get; init; }
}
