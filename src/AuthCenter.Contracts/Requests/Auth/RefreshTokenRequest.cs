namespace AuthCenter.Contracts.Requests.Auth;

public class RefreshTokenRequest
{
    public string RefreshToken { get; init; } = string.Empty;
    public string? ApplicationCode { get; init; }
}
