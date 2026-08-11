namespace AuthCenter.Contracts.Requests.Auth;

public sealed class RegisterPasskeyRequest
{
    public string CredentialJson { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
}
