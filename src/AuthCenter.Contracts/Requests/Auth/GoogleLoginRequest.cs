namespace AuthCenter.Contracts.Requests.Auth;

public class GoogleLoginRequest
{
    public string IdToken { get; init; } = string.Empty;
    public string ApplicationCode { get; init; } = string.Empty;
}
