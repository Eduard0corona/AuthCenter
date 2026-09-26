namespace AuthCenter.Api.Services;

/// <summary>Fixed-window counters for a single instance; expired windows are pruned as it runs.</summary>
public sealed class InMemoryRateLimitStore : IRateLimitStore
{
    private readonly Dictionary<string, (DateTime ExpiresAt, int Count)> _windows = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private int _operations;

    public Task<(bool Acquired, TimeSpan RetryAfter)> TryAcquireAsync(string key, int permitLimit, TimeSpan window, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            if (++_operations % 1024 == 0)
            {
                foreach (var expired in _windows.Where(item => item.Value.ExpiresAt <= now).Select(item => item.Key).ToList())
                    _windows.Remove(expired);
            }

            if (!_windows.TryGetValue(key, out var current) || current.ExpiresAt <= now)
            {
                _windows[key] = (now.Add(window), 1);
                return Task.FromResult((true, TimeSpan.Zero));
            }

            if (current.Count >= permitLimit)
                return Task.FromResult((false, current.ExpiresAt - now));

            _windows[key] = (current.ExpiresAt, current.Count + 1);
            return Task.FromResult((true, TimeSpan.Zero));
        }
    }
}
