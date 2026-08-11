namespace AuthCenter.Contracts.Requests.Auth;

public sealed class BeginPasskeyLoginRequest
{
    public string ApplicationCode { get; init; } = string.Empty;
    public string? Email { get; init; }
}
