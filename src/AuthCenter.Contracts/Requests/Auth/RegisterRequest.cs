namespace AuthCenter.Contracts.Requests.Auth;

public class RegisterRequest
{
    public string FullName { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
    public string ApplicationCode { get; init; } = string.Empty;
}
