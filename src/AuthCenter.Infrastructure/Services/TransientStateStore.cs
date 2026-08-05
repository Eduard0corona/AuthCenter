using AuthCenter.Application.Interfaces;
using AuthCenter.Domain.Entities;
using AuthCenter.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuthCenter.Infrastructure.Services;

/// <summary>
/// Database-backed implementation, so the state survives a restart and is seen by every instance.
/// Uses its own DbContext for the same reason <see cref="AuditService"/> does: writing a
/// single-use marker must not flush whatever the caller happens to have pending.
/// </summary>
public class TransientStateStore : ITransientStateStore
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(5);
    private static readonly object SweepGate = new();
    private static DateTime _lastSweepUtc = DateTime.MinValue;

    private readonly IDbContextFactory<AuthCenterDbContext> _dbFactory;
    private readonly IDateTimeProvider _dateTimeProvider;

    public TransientStateStore(
        IDbContextFactory<AuthCenterDbContext> dbFactory,
        IDateTimeProvider dateTimeProvider)
    {
        _dbFactory = dbFactory;
        _dateTimeProvider = dateTimeProvider;
    }

    public async Task<bool> TryConsumeAsync(string purpose, string key, DateTime expiresAt, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var now = _dateTimeProvider.UtcNow;

        var existing = await db.TransientStates
            .FirstOrDefaultAsync(s => s.Purpose == purpose && s.Key == key, ct);

        if (existing is not null)
        {
            // An expired marker is indistinguishable from never having been consumed: the token it
            // guarded cannot be redeemed any more either.
            if (existing.ExpiresAt > now)
                return false;

            db.TransientStates.Remove(existing);
            await db.SaveChangesAsync(ct);
        }

        db.TransientStates.Add(new TransientState
        {
            Id = Guid.NewGuid(),
            Purpose = purpose,
            Key = key,
            Value = null,
            CreatedAt = now,
            ExpiresAt = expiresAt
        });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Lost the race against another instance that consumed the same key first. The unique
            // index is what makes this safe; the check above only keeps the common path cheap.
            return false;
        }

        await SweepExpiredAsync(ct);
        return true;
    }

    public async Task<bool> IsConsumedAsync(string purpose, string key, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var now = _dateTimeProvider.UtcNow;

        return await db.TransientStates
            .AsNoTracking()
            .AnyAsync(s => s.Purpose == purpose && s.Key == key && s.ExpiresAt > now, ct);
    }

    public async Task SetAsync(string purpose, string key, string value, DateTime expiresAt, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var now = _dateTimeProvider.UtcNow;

        var existing = await db.TransientStates
            .FirstOrDefaultAsync(s => s.Purpose == purpose && s.Key == key, ct);

        if (existing is not null)
        {
            existing.Value = value;
            existing.CreatedAt = now;
            existing.ExpiresAt = expiresAt;
        }
        else
        {
            db.TransientStates.Add(new TransientState
            {
                Id = Guid.NewGuid(),
                Purpose = purpose,
                Key = key,
                Value = value,
                CreatedAt = now,
                ExpiresAt = expiresAt
            });
        }

        await db.SaveChangesAsync(ct);
        await SweepExpiredAsync(ct);
    }

    public async Task<string?> GetAsync(string purpose, string key, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var now = _dateTimeProvider.UtcNow;

        return await db.TransientStates
            .AsNoTracking()
            .Where(s => s.Purpose == purpose && s.Key == key && s.ExpiresAt > now)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<string?> TakeAsync(string purpose, string key, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var now = _dateTimeProvider.UtcNow;

        var entry = await db.TransientStates
            .FirstOrDefaultAsync(s => s.Purpose == purpose && s.Key == key, ct);

        if (entry is null)
            return null;

        db.TransientStates.Remove(entry);
        await db.SaveChangesAsync(ct);

        return entry.ExpiresAt > now ? entry.Value : null;
    }

    public async Task RemoveAsync(string purpose, string key, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var entry = await db.TransientStates
            .FirstOrDefaultAsync(s => s.Purpose == purpose && s.Key == key, ct);

        if (entry is null)
            return;

        db.TransientStates.Remove(entry);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Drops expired rows, at most once every few minutes per instance, so this table does not
    /// become another one that only ever grows.
    /// </summary>
    private async Task SweepExpiredAsync(CancellationToken ct)
    {
        var now = _dateTimeProvider.UtcNow;

        lock (SweepGate)
        {
            if (now - _lastSweepUtc < SweepInterval)
                return;

            _lastSweepUtc = now;
        }

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            await db.TransientStates.Where(s => s.ExpiresAt <= now).ExecuteDeleteAsync(ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Housekeeping must never fail the request that happened to trigger it.
        }
    }
}
