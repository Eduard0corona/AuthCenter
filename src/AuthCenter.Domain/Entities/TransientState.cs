namespace AuthCenter.Domain.Entities;

/// <summary>
/// Short-lived state that must be shared by every instance of the service: single-use markers for
/// tokens that may only be redeemed once, and the codes and sessions that outlive a single request
/// but not the flow they belong to. Keeping this in process memory would mean a second instance
/// silently stops enforcing any of it.
/// </summary>
public class TransientState
{
    public Guid Id { get; set; }

    /// <summary>Namespace for the key, so unrelated flows cannot collide.</summary>
    public string Purpose { get; set; } = string.Empty;

    public string Key { get; set; } = string.Empty;

    /// <summary>Null for entries that only record that something was consumed.</summary>
    public string? Value { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
}
