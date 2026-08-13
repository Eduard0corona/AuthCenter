using AuthCenter.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuthCenter.Infrastructure.Services;

public static class SuperAdminInvariantLock
{
    public static Task AcquireAsync(AuthCenterDbContext db, CancellationToken ct = default) => db.Database.IsRelational()
        ? db.Database.ExecuteSqlRawAsync("DECLARE @result int; EXEC @result = sp_getapplock @Resource = 'AuthCenter.SuperAdminInvariant', @LockMode = 'Exclusive', @LockOwner = 'Transaction', @LockTimeout = 30000; IF @result < 0 THROW 51000, 'Could not acquire the SuperAdmin invariant lock.', 1;", ct)
        : Task.CompletedTask;
}
