namespace AuthCenter.Domain.Entities;

public sealed class DistributedRateLimitBucket
{
    public string Key { get; set; } = string.Empty;
    public DateTime WindowStartedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public int PermitCount { get; set; }
}
