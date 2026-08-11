using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace AuthCenter.Infrastructure.Persistence;

public sealed class AuthCenterDbContextFactory : IDesignTimeDbContextFactory<AuthCenterDbContext>
{
    public AuthCenterDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
            ?? "Server=(localdb)\\mssqllocaldb;Database=AuthCenter;Trusted_Connection=True;MultipleActiveResultSets=true";

        // Identity reads its schema version from the application provider while building the EF
        // model. Supply the same v3 option used at runtime so migrations include passkey storage.
        var services = new ServiceCollection();
        services.Configure<IdentityOptions>(options =>
        {
            options.Stores.SchemaVersion = IdentitySchemaVersions.Version3;
            options.Stores.MaxLengthForKeys = 450;
        });
        var applicationServices = services.BuildServiceProvider();

        var options = new DbContextOptionsBuilder<AuthCenterDbContext>()
            .UseSqlServer(connectionString, sql =>
                sql.MigrationsAssembly(typeof(AuthCenterDbContext).Assembly.FullName))
            .UseApplicationServiceProvider(applicationServices)
            .Options;

        return new AuthCenterDbContext(options);
    }
}
