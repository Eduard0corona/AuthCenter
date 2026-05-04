namespace AuthCenter.Contracts.Requests.Auth;

public class VerifyMagicLinkRequest
{
    public string Token { get; init; } = string.Empty;
    public string ApplicationCode { get; init; } = string.Empty;
    public string? DeviceToken { get; init; }
}
