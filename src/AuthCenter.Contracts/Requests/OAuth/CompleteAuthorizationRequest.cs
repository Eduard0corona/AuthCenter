namespace AuthCenter.Contracts.Requests.OAuth;

public class CompleteAuthorizationRequest
{
    public string? InteractionId { get; init; }
    public bool Consent { get; init; } = true;
}
