namespace AuthCenter.Contracts.Requests.Auth;

public class VerifyMfaRequest
{
    public string MfaPendingToken { get; init; } = string.Empty;
    public string? TotpCode { get; init; }
    public string? BackupCode { get; init; }
    public string? EmailOtpCode { get; init; }
    public bool TrustDevice { get; init; }
}
