namespace AuthCenter.Api.Services;

/// <summary>Fixed-window counters keyed by an opaque, hashed partition key.</summary>
public interface IRateLimitStore
{
    Task<(bool Acquired, TimeSpan RetryAfter)> TryAcquireAsync(string key, int permitLimit, TimeSpan window, CancellationToken ct);
}
