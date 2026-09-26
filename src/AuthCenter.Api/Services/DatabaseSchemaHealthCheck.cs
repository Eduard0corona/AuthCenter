using AuthCenter.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AuthCenter.Api.Services;

/// <summary>
/// Readiness of the database schema. The application's database identity cannot change the schema,
/// so migrations are applied out of band before a deployment: an instance whose build expects
/// migrations the database lacks is not ready. Migrations the database has but this build does not
/// know (a rollback to an older build) only degrade it.
/// </summary>
public sealed class DatabaseSchemaHealthCheck(AuthCenterDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!db.Database.IsRelational())
            return HealthCheckResult.Healthy("The database has no migrations to check.");
        var known = db.Database.GetMigrations().ToList();
        var applied = (await db.Database.GetAppliedMigrationsAsync(cancellationToken)).ToList();
        var pending = known.Except(applied, StringComparer.Ordinal).ToList();
        var unknown = applied.Except(known, StringComparer.Ordinal).ToList();
        var data = new Dictionary<string, object>
        {
            ["latestKnown"] = known.LastOrDefault() ?? string.Empty,
            ["latestApplied"] = applied.LastOrDefault() ?? string.Empty,
            ["pending"] = pending.Count,
            ["unknown"] = unknown.Count
        };
        if (pending.Count > 0)
            return HealthCheckResult.Unhealthy($"{pending.Count} database migration(s) pending, the first {pending[0]}. Apply them before routing traffic here.", data: data);
        if (unknown.Count > 0)
            return HealthCheckResult.Degraded($"The database has {unknown.Count} migration(s) this build does not know, the newest {unknown[^1]}.", data: data);
        return HealthCheckResult.Healthy("The database schema is current.", data);
    }
}
