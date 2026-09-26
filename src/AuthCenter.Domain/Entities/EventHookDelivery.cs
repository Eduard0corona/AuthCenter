namespace AuthCenter.Domain.Entities;

public sealed class EventHookDelivery
{
    public Guid Id { get; set; }
    public Guid EventHookId { get; set; }
    public Guid EventId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;

    /// <summary>When the event occurred and the delivery was queued.</summary>
    public DateTime CreatedAt { get; set; }
    public int AttemptCount { get; set; }
    public DateTime NextAttemptAt { get; set; }
    public DateTime? LockedUntil { get; set; }
    public DateTime? DeliveredAt { get; set; }
    public DateTime? DeadLetteredAt { get; set; }
    public string? LastError { get; set; }
    public string? LastReplayIdempotencyKey { get; set; }
    public EventHook EventHook { get; set; } = null!;
}
