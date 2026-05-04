namespace AuthCenter.Contracts.Requests.Auth;

public class RequestEmailChangeRequest
{
    public string NewEmail { get; init; } = string.Empty;
    public string? CallbackBaseUrl { get; init; }
}
