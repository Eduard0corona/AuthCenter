namespace AuthCenter.Contracts.Requests.Auth;

public class RegenerateBackupCodesRequest
{
    public string TotpCode { get; init; } = string.Empty;
}
