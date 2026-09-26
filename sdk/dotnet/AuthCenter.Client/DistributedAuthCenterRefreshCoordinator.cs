using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Distributed;

namespace AuthCenter.Client;

/// <summary>
/// Refresh coordination for BFFs that run on several instances. AuthCenter rotates refresh tokens
/// and treats a second use of the same token as theft, revoking the whole session, so two
/// instances refreshing one session at once would sign the user out. Through the shared
/// <see cref="IDistributedCache"/> (the one that already stores the BFF sessions) one instance
/// refreshes while the others wait for its result, which is shared briefly and encrypted with
/// Data Protection (the key ring must be shared by every instance, as for the session cookie).
/// </summary>
/// <remarks>
/// <see cref="IDistributedCache"/> has no atomic "add if absent", so the lock is claimed, then
/// read back after a short settle delay: of two instances that write at almost the same time,
/// only the last writer proceeds. For a strict guarantee implement
/// <see cref="IAuthCenterRefreshCoordinator"/> over a store with atomic locks.
/// </remarks>
public sealed class DistributedAuthCenterRefreshCoordinator : IAuthCenterRefreshCoordinator
{
    private const string KeyPrefix = "AuthCenter.Client.Refresh:";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IDistributedCache _cache;
    private readonly IDataProtector _protector;
    private readonly InMemoryAuthCenterRefreshCoordinator _local = new();
    private readonly DistributedRefreshCoordinationOptions _options;

    public DistributedAuthCenterRefreshCoordinator(IDistributedCache cache, IDataProtectionProvider dataProtection, DistributedRefreshCoordinationOptions? options = null)
    {
        _cache = cache;
        _protector = dataProtection.CreateProtector("AuthCenter.Client.RefreshCoordination.v1");
        _options = options ?? new DistributedRefreshCoordinationOptions();
    }

    public Task<OAuthTokenSet> CoordinateAsync(
        string refreshToken,
        Func<CancellationToken, Task<OAuthTokenSet>> refreshOperation,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);
        ArgumentNullException.ThrowIfNull(refreshOperation);
        // Requests of this instance are merged first; only one of them talks to the other instances.
        return _local.CoordinateAsync(refreshToken, _ => CoordinateAcrossInstancesAsync(refreshToken, refreshOperation), cancellationToken);
    }

    private async Task<OAuthTokenSet> CoordinateAcrossInstancesAsync(string refreshToken, Func<CancellationToken, Task<OAuthTokenSet>> refreshOperation)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken)));
        var lockKey = KeyPrefix + "lock:" + key;
        var resultKey = KeyPrefix + "result:" + key;
        var deadline = DateTimeOffset.UtcNow.Add(_options.WaitTimeout);

        while (true)
        {
            if (await ReadResultAsync(resultKey) is { } shared)
                return shared.Unwrap();

            if (await _cache.GetStringAsync(lockKey) is null)
            {
                var owner = Guid.NewGuid().ToString("N");
                await _cache.SetStringAsync(lockKey, owner, new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = _options.LockLifetime });
                await Task.Delay(_options.SettleDelay);
                if (await _cache.GetStringAsync(lockKey) == owner)
                    return await RefreshAsOwnerAsync(refreshOperation, lockKey, resultKey);
            }

            if (DateTimeOffset.UtcNow >= deadline)
                throw new HttpRequestException("Another instance did not finish refreshing the AuthCenter session in time.");
            await Task.Delay(_options.PollInterval);
        }
    }

    private async Task<OAuthTokenSet> RefreshAsOwnerAsync(Func<CancellationToken, Task<OAuthTokenSet>> refreshOperation, string lockKey, string resultKey)
    {
        var entry = new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = _options.ResultLifetime };
        try
        {
            var tokens = await refreshOperation(CancellationToken.None);
            await _cache.SetAsync(resultKey, _protector.Protect(JsonSerializer.SerializeToUtf8Bytes(new SharedResult(tokens, null), Json)), entry);
            return tokens;
        }
        catch (HttpRequestException exception)
        {
            // Waiting instances fail the same way instead of replaying the rejected refresh token.
            await _cache.SetAsync(resultKey, _protector.Protect(JsonSerializer.SerializeToUtf8Bytes(new SharedResult(null, exception.Message), Json)), entry);
            throw;
        }
        finally
        {
            await _cache.RemoveAsync(lockKey);
        }
    }

    private async Task<SharedResult?> ReadResultAsync(string resultKey)
    {
        var protectedResult = await _cache.GetAsync(resultKey);
        if (protectedResult is null)
            return null;
        try
        {
            return JsonSerializer.Deserialize<SharedResult>(_protector.Unprotect(protectedResult), Json);
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException)
        {
            return null;
        }
    }

    private sealed record SharedResult(OAuthTokenSet? Tokens, string? Error)
    {
        public OAuthTokenSet Unwrap() => Tokens ?? throw new HttpRequestException(Error ?? "AuthCenter rejected the refresh token.");
    }
}

/// <summary>Timings of <see cref="DistributedAuthCenterRefreshCoordinator"/>.</summary>
public sealed class DistributedRefreshCoordinationOptions
{
    /// <summary>How long a claimed lock lives if its owner stops (default 20 seconds, longer than a token request).</summary>
    public TimeSpan LockLifetime { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>How long the refreshed tokens stay available to the other instances (default 30 seconds).</summary>
    public TimeSpan ResultLifetime { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Delay before reading a claimed lock back (default 150 ms); larger than the cache's write skew.</summary>
    public TimeSpan SettleDelay { get; init; } = TimeSpan.FromMilliseconds(150);

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>How long an instance waits for another one's refresh (default 25 seconds, so a lock left by a stopped instance can expire first).</summary>
    public TimeSpan WaitTimeout { get; init; } = TimeSpan.FromSeconds(25);
}
