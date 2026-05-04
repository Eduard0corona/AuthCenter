namespace AuthCenter.Contracts.Requests.Auth;

public class DisableMfaRequest
{
    public string? TotpCode { get; init; }
    public string? BackupCode { get; init; }
    public string? EmailOtpCode { get; init; }
}
