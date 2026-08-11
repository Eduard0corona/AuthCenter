namespace AuthCenter.Domain.Entities;

public sealed class EventHook
{
    public Guid Id { get; set; }
    public Guid? ApplicationSystemId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string ProtectedSecret { get; set; } = string.Empty;
    public string EventTypesJson { get; set; } = "[]";
    public bool IsVerified { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; }
    public DateTime? VerifiedAt { get; set; }
    public ApplicationSystem? ApplicationSystem { get; set; }
    public ICollection<EventHookDelivery> Deliveries { get; set; } = new List<EventHookDelivery>();
}
