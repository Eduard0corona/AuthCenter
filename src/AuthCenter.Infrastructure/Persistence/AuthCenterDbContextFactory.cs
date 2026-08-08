using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AuthCenter.Infrastructure.Persistence;

public sealed class AuthCenterDbContextFactory : IDesignTimeDbContextFactory<AuthCenterDbContext>
{
    public AuthCenterDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
            ?? "Server=(localdb)\\mssqllocaldb;Database=AuthCenter;Trusted_Connection=True;MultipleActiveResultSets=true";

        var options = new DbContextOptionsBuilder<AuthCenterDbContext>()
            .UseSqlServer(connectionString, sql =>
                sql.MigrationsAssembly(typeof(AuthCenterDbContext).Assembly.FullName))
            .Options;

        return new AuthCenterDbContext(options);
    }
}
