namespace AuthCenter.Contracts.Responses.Auth;

public class ForcedChangePendingResponse
{
    public string ForcedChangePendingToken { get; init; } = string.Empty;
    public int ExpiresIn { get; init; }
    public bool PasswordChangeRequired { get; init; } = true;
}
