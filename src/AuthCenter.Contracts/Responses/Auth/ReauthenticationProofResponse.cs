namespace AuthCenter.Contracts.Responses.Auth;

public sealed class ReauthenticationProofResponse
{
    public string ProofToken { get; init; } = string.Empty;
    public string AssuranceLevel { get; init; } = string.Empty;
    public int ExpiresIn { get; init; }
}
