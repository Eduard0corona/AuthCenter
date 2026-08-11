namespace AuthCenter.Contracts.Requests.Auth;

public sealed class CompletePasskeyLoginRequest
{
    public string InteractionId { get; init; } = string.Empty;
    public string CredentialJson { get; init; } = string.Empty;
}
