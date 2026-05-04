namespace AuthCenter.Contracts.Requests.Auth;

public class ConfirmEmailChangeRequest
{
    public Guid UserId { get; init; }
    public string NewEmail { get; init; } = string.Empty;
    public string Token { get; init; } = string.Empty;
}
