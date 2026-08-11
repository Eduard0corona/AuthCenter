using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace AuthCenter.Client;

public sealed class InMemoryAuthCenterRefreshCoordinator : IAuthCenterRefreshCoordinator
{
    private static readonly TimeSpan ReplayWindow = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<string, RefreshOperation> _operations = new(StringComparer.Ordinal);

    public async Task<OAuthTokenSet> CoordinateAsync(
        string refreshToken,
        Func<CancellationToken, Task<OAuthTokenSet>> refreshOperation,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);
        ArgumentNullException.ThrowIfNull(refreshOperation);

        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken)));
        var operation = _operations.GetOrAdd(key, _ => new RefreshOperation(refreshOperation));
        var task = operation.Task.Value;
        _ = task.ContinueWith(
            _ => ScheduleCleanup(key, operation),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return await task.WaitAsync(cancellationToken);
    }

    private void ScheduleCleanup(string key, RefreshOperation operation)
    {
        if (Interlocked.Exchange(ref operation.CleanupScheduled, 1) == 0)
            _ = RemoveAfterReplayWindowAsync(key, operation);
    }

    private async Task RemoveAfterReplayWindowAsync(string key, RefreshOperation operation)
    {
        await Task.Delay(ReplayWindow);
        _operations.TryRemove(new KeyValuePair<string, RefreshOperation>(key, operation));
    }

    private sealed class RefreshOperation(Func<CancellationToken, Task<OAuthTokenSet>> operation)
    {
        public Lazy<Task<OAuthTokenSet>> Task { get; } = new(
            () => operation(CancellationToken.None),
            LazyThreadSafetyMode.ExecutionAndPublication);

        public int CleanupScheduled;
    }
}
