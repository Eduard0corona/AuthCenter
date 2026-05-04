namespace AuthCenter.Contracts.Requests.Auth;

public class GitHubLoginRequest
{
    public string AccessToken { get; init; } = string.Empty;
    public string ApplicationCode { get; init; } = string.Empty;
    public string? DeviceToken { get; init; }
}
