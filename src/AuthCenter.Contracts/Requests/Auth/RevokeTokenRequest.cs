namespace AuthCenter.Contracts.Requests.Auth;

public class RevokeTokenRequest
{
    public string RefreshToken { get; init; } = string.Empty;
}
