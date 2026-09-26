using AuthCenter.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

namespace AuthCenter.IntegrationTests;

/// <summary>
/// The API with its production data access: SQL Server through the application's own registration
/// (connection pooling and the retrying execution strategy included), on an isolated database that is
/// migrated before the host starts, seeded at startup and dropped on dispose. Use it with <see cref="RelationalFactAttribute"/>.
/// </summary>
public sealed class SqlServerWebApplicationFactory : WebApplicationFactory<Program>
{
    public const string AdminEmail = "admin.relational@example.com";
    public static string AdminPassword { get; } = TestSecretGenerator.CreatePassword();

    private static readonly string MfaEncryptionKey = TestSecretGenerator.CreateKey();
    private readonly string _connectionString = SqlServerHardeningTests.BuildIsolatedConnectionString();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        // Host settings, unlike app configuration callbacks, are in place before Program reads the
        // connection string to register the DbContext.
        foreach (var (key, value) in new Dictionary<string, string>
        {
            ["ConnectionStrings:DefaultConnection"] = _connectionString,
            ["Database:MigrateOnStartup"] = "false",
            ["Database:SeedOnStartup"] = "true",
            ["Seed:AdminEmail"] = AdminEmail,
            ["Seed:AdminPassword"] = AdminPassword,
            ["Seed:AdminFullName"] = "Relational Admin",
            ["Mfa:EncryptionKey"] = MfaEncryptionKey,
            ["Mfa:TotpIssuer"] = "AuthCenter",
            ["Jwt:RsaPrivateKeyPem"] = TestRsaKey.PrivateKeyPem,
            ["Passkeys:RelyingPartyId"] = "localhost",
            ["Passkeys:AllowedOrigins:0"] = "http://localhost"
        })
            builder.UseSetting(key, value);
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // The schema is created as the other relational tests create it. Migrating from the app would
        // check the model against EF's process-wide cache, which parallel test hosts share.
        using (var db = new AuthCenterDbContext(SqlServerHardeningTests.CreateOptions(_connectionString)))
            db.Database.Migrate();
        return base.CreateHost(builder);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing)
            return;
        using var cleanup = new AuthCenterDbContext(SqlServerHardeningTests.CreateOptions(_connectionString));
        cleanup.Database.EnsureDeleted();
    }
}
