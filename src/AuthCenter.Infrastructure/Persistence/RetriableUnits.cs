using Microsoft.EntityFrameworkCore;

namespace AuthCenter.Infrastructure.Persistence;

internal static class RetriableUnits
{
    /// <summary>
    /// Runs an operation that opens its own transaction. On SQL Server the context retries transient
    /// faults, and EF Core refuses a transaction opened outside the execution strategy; inside it, a
    /// retry repeats the whole unit, from a clean change tracker so nothing of the failed attempt is
    /// saved with it.
    /// </summary>
    public static Task<T> RunRetriableAsync<T>(this AuthCenterDbContext db, Func<Task<T>> operation)
    {
        var attempt = 0;
        return db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            if (attempt++ > 0)
                db.ChangeTracker.Clear();
            return await operation();
        });
    }
}
