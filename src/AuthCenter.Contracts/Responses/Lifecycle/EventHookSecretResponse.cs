namespace AuthCenter.Contracts.Responses.Lifecycle;

/// <summary>A hook's signing secret, returned once when it is created or rotated.</summary>
public sealed class EventHookSecretResponse
{
    public Guid Id { get; init; }
    public string Secret { get; init; } = string.Empty;
    public bool IsVerified { get; init; }

    /// <summary>After a rotation: until when deliveries are also signed with the previous secret.</summary>
    public DateTime? PreviousSecretExpiresAt { get; init; }
}
