namespace AuthCenter.Contracts.Requests.Auth;

public class ForcedChangePasswordRequest
{
    public string ForcedChangePendingToken { get; init; } = string.Empty;
    public string NewPassword { get; init; } = string.Empty;
}
