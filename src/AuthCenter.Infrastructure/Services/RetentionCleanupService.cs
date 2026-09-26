using AuthCenter.Infrastructure.Persistence;
using AuthCenter.Infrastructure.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AuthCenter.Infrastructure.Services;

public sealed class RetentionCleanupService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RetentionSettings _settings;
    private readonly ILogger<RetentionCleanupService> _logger;

    public RetentionCleanupService(
        IServiceScopeFactory scopeFactory,
        IOptions<RetentionSettings> settings,
        ILogger<RetentionCleanupService> logger)
    {
        _scopeFactory = scopeFactory;
        _settings = settings.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_settings.Enabled)
            return;

        using var timer = new PeriodicTimer(TimeSpan.FromHours(Math.Clamp(_settings.IntervalHours, 1, 24)));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await CleanupAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Retention cleanup failed");
            }
        }
    }

    internal async Task CleanupAsync(CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
        var now = DateTime.UtcNow;
        var tokenCutoff = now.AddDays(-Math.Clamp(_settings.TokenHistoryDays, 1, 365));
        var auditCutoff = now.AddDays(-Math.Clamp(_settings.AuditLogDays, 30, 3650));
        var batchSize = Math.Clamp(_settings.BatchSize, 50, 5000);

        var removed = 0;
        removed += await DeleteBatchesAsync(db, db.TransientStates.Where(item => item.ExpiresAt < now), batchSize, ct);
        removed += await DeleteBatchesAsync(db, db.OAuthAuthorizationCodes.Where(item => item.ExpiresAt < tokenCutoff), batchSize, ct);
        removed += await DeleteBatchesAsync(db, db.RefreshTokens.Where(item => item.ExpiresAt < tokenCutoff || (item.RevokedAt != null && item.RevokedAt < tokenCutoff)), batchSize, ct);
        removed += await DeleteBatchesAsync(db, db.SingleSignOnSessionClients.Where(item => !db.RefreshTokens.Any(token => token.Id == item.SessionId)), batchSize, ct);
        removed += await DeleteBatchesAsync(db, db.UserTrustedDevices.Where(item => item.ExpiresAt < now), batchSize, ct);
        removed += await DeleteBatchesAsync(db, db.AuditLogs.Where(item => item.CreatedAt < auditCutoff), batchSize, ct);
        removed += await DeleteBatchesAsync(db, db.DistributedRateLimitBuckets.Where(item => item.ExpiresAt < now), batchSize, ct);
        removed += await DeleteBatchesAsync(db, db.OutboxMessages.Where(item => item.ProcessedAt != null && item.ProcessedAt < tokenCutoff), batchSize, ct);
        removed += await DeleteBatchesAsync(db, db.AuthenticationObservations.Where(item => item.ExpiresAt < now), batchSize, ct);
        removed += await DeleteBatchesAsync(db, db.ProvisioningTokens.Where(item => item.ExpiresAt < tokenCutoff || (item.RevokedAt != null && item.RevokedAt < tokenCutoff)), batchSize, ct);
        removed += await DeleteBatchesAsync(db, db.EventHookDeliveries.Where(item => (item.DeliveredAt != null && item.DeliveredAt < tokenCutoff) || (item.DeadLetteredAt != null && item.DeadLetteredAt < auditCutoff)), batchSize, ct);

        if (removed > 0)
            _logger.LogInformation("Retention cleanup removed {RemovedCount} expired records", removed);
    }

    private static async Task<int> DeleteBatchesAsync<TEntity>(
        AuthCenterDbContext db,
        IQueryable<TEntity> source,
        int batchSize,
        CancellationToken ct)
        where TEntity : class
    {
        var total = 0;
        while (true)
        {
            var batch = await source.Take(batchSize).ToListAsync(ct);
            if (batch.Count == 0)
                return total;

            db.RemoveRange(batch);
            await db.SaveChangesAsync(ct);
            total += batch.Count;
            if (batch.Count < batchSize)
                return total;
        }
    }
}
