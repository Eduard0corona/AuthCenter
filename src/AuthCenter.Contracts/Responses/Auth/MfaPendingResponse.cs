namespace AuthCenter.Contracts.Responses.Auth;

public class MfaPendingResponse
{
    public string MfaPendingToken { get; init; } = string.Empty;
    public int ExpiresIn { get; init; }
    public bool MfaRequired { get; init; } = true;
}
