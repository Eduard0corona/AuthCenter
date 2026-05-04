namespace AuthCenter.Contracts.Requests.Auth;

public class SendMfaEmailOtpRequest
{
    public string MfaPendingToken { get; init; } = string.Empty;
}
