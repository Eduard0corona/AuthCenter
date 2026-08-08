using System.Data;
using AuthCenter.Domain.Entities;
using AuthCenter.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuthCenter.Api.Services;

public sealed class DistributedRateLimitStore
{
    private readonly AuthCenterDbContext _db;

    public DistributedRateLimitStore(AuthCenterDbContext db)
    {
        _db = db;
    }

    public async Task<(bool Acquired, TimeSpan RetryAfter)> TryAcquireAsync(
        string key,
        int permitLimit,
        TimeSpan window,
        CancellationToken ct)
    {
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            var now = DateTime.UtcNow;
            await using var transaction = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var bucket = await _db.DistributedRateLimitBuckets.SingleOrDefaultAsync(item => item.Key == key, ct);

            if (bucket is null)
            {
                bucket = new DistributedRateLimitBucket
                {
                    Key = key,
                    WindowStartedAt = now,
                    ExpiresAt = now.Add(window),
                    PermitCount = 1
                };
                _db.DistributedRateLimitBuckets.Add(bucket);
                await _db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return (true, TimeSpan.Zero);
            }

            if (bucket.ExpiresAt <= now)
            {
                bucket.WindowStartedAt = now;
                bucket.ExpiresAt = now.Add(window);
                bucket.PermitCount = 1;
                await _db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                return (true, TimeSpan.Zero);
            }

            if (bucket.PermitCount >= permitLimit)
            {
                await transaction.CommitAsync(ct);
                return (false, bucket.ExpiresAt - now);
            }

            bucket.PermitCount++;
            await _db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return (true, TimeSpan.Zero);
        });
    }
}
