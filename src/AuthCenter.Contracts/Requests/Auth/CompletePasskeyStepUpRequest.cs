namespace AuthCenter.Contracts.Requests.Auth;

public sealed class CompletePasskeyStepUpRequest
{
    public string InteractionId { get; init; } = string.Empty;
    public string Purpose { get; init; } = string.Empty;
    public string CredentialJson { get; init; } = string.Empty;
}
