namespace AuthCenter.Contracts.Responses.Auth;

public class MfaSetupResponse
{
    public string TotpUri { get; init; } = string.Empty;
    public string SecretBase32 { get; init; } = string.Empty;
}
