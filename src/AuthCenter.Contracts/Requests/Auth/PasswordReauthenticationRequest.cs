namespace AuthCenter.Contracts.Requests.Auth;

public sealed class PasswordReauthenticationRequest
{
    public string Purpose { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
}
