namespace AuthCenter.Domain.Entities;

public sealed class EventHook
{
    public Guid Id { get; set; }
    public Guid? ApplicationSystemId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string ProtectedSecret { get; set; } = string.Empty;

    /// <summary>The secret replaced by the last rotation, still signing deliveries until it expires.</summary>
    public string? PreviousProtectedSecret { get; set; }
    public DateTime? PreviousSecretExpiresAt { get; set; }
    public string EventTypesJson { get; set; } = "[]";
    public bool IsVerified { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime? VerifiedAt { get; set; }
    public long Version { get; set; } = 1;
    public ApplicationSystem? ApplicationSystem { get; set; }
    public ICollection<EventHookDelivery> Deliveries { get; set; } = new List<EventHookDelivery>();
}
