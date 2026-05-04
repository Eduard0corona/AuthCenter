namespace AuthCenter.Contracts.Requests.Auth;

public class EnableMfaRequest
{
    public string TotpCode { get; init; } = string.Empty;
}
