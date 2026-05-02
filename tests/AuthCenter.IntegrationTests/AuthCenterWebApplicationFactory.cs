using AuthCenter.Domain.Entities;
using AuthCenter.Infrastructure.Persistence;
using AuthCenter.Infrastructure.Persistence.Seed;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AuthCenter.IntegrationTests;

public class AuthCenterWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string AdminEmail = "admin.integration@example.com";
    public const string AdminPassword = "Admin12345";

    private readonly InMemoryDatabaseRoot _databaseRoot = new();
    private readonly string _databaseName = "AuthCenterTest_" + Guid.NewGuid();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Seed:AdminEmail"] = AdminEmail,
                ["Seed:AdminPassword"] = AdminPassword,
                ["Seed:AdminFullName"] = "Integration Admin"
            });
        });

        builder.ConfigureServices(services =>
        {
            var dbContextDescriptors = services
                .Where(d =>
                    d.ServiceType == typeof(AuthCenterDbContext) ||
                    d.ServiceType == typeof(DbContextOptions) ||
                    d.ServiceType == typeof(DbContextOptions<AuthCenterDbContext>) ||
                    d.ServiceType.FullName?.Contains("IDbContextOptionsConfiguration", StringComparison.Ordinal) == true)
                .ToList();

            foreach (var descriptor in dbContextDescriptors)
                services.Remove(descriptor);

            services.AddDbContext<AuthCenterDbContext>(options =>
                options.UseInMemoryDatabase(_databaseName, _databaseRoot));
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);

        using var scope = host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthCenterDbContext>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("TestSeed");

        AuthCenterSeeder
            .SeedAsync(db, userManager, roleManager, configuration, logger)
            .GetAwaiter()
            .GetResult();

        return host;
    }
}
