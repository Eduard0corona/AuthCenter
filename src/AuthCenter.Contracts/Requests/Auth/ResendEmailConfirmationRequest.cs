namespace AuthCenter.Contracts.Requests.Auth;

public class ResendEmailConfirmationRequest
{
    public string Email { get; init; } = string.Empty;
    public string ApplicationCode { get; init; } = string.Empty;
    public string? CallbackBaseUrl { get; init; }
}
