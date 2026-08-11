namespace AuthCenter.Contracts.Requests.Auth;

public sealed class BeginPasskeyStepUpRequest
{
    public string Purpose { get; init; } = string.Empty;
}
