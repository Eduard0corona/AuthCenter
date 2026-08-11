using AuthCenter.Api.Services;
using AuthCenter.Domain.Entities;
using AuthCenter.Infrastructure.Persistence;
using AuthCenter.Infrastructure.Services;
using AuthCenter.Infrastructure.Settings;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AuthCenter.IntegrationTests;

public sealed class SqlServerHardeningTests
{
    [Fact]
    public async Task IdempotentDeploymentScript_UpgradesProductionBaseline()
    {
        if (!OperatingSystem.IsWindows() && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AUTHCENTER_RELATIONAL_TEST_CONNECTION")))
            return;

        var connectionString = BuildIsolatedConnectionString();
        var options = CreateOptions(connectionString);
        try
        {
            string deploymentScript;
            await using (var db = new AuthCenterDbContext(options))
            {
                var migrator = db.Database.GetService<IMigrator>();
                await migrator.MigrateAsync("20260808033000_AddOutbox");
                deploymentScript = migrator.GenerateScript(
                    options: MigrationsSqlGenerationOptions.Idempotent);
            }

            await using (var connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync();
                foreach (var batch in System.Text.RegularExpressions.Regex.Split(
                    deploymentScript,
                    "(?im)^\\s*GO\\s*$"))
                {
                    if (string.IsNullOrWhiteSpace(batch))
                        continue;
                    await using var command = connection.CreateCommand();
                    command.CommandTimeout = 180;
                    command.CommandText = batch;
                    await command.ExecuteNonQueryAsync();
                }
            }

            await using var verifyDb = new AuthCenterDbContext(options);
            var applied = await verifyDb.Database
                .SqlQueryRaw<string>("SELECT MigrationId AS Value FROM dbo.__EFMigrationsHistory")
                .ToListAsync();
            Assert.Equal(18, applied.Count);
            Assert.Contains("20260811070000_CompleteOktaPhase1", applied);
            Assert.Contains("20260811091450_AddIdentityPasskeysPhase2", applied);
            Assert.Contains("20260811092337_CompleteAdaptiveAuthenticationPhase2", applied);
        }
        finally
        {
            await using var cleanupDb = new AuthCenterDbContext(options);
            await cleanupDb.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task Phase1Migration_PreservesExistingRulesAsPublishedVersion()
    {
        if (!OperatingSystem.IsWindows() && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AUTHCENTER_RELATIONAL_TEST_CONNECTION")))
            return;

        var connectionString = BuildIsolatedConnectionString();
        var options = CreateOptions(connectionString);
        try
        {
            await using var db = new AuthCenterDbContext(options);
            var migrator = db.Database.GetService<IMigrator>();
            await migrator.MigrateAsync("20260810160017_AddApplicationAccessPolicies");

            var applicationId = Guid.NewGuid();
            var ruleId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            await db.Database.ExecuteSqlInterpolatedAsync($$"""
                INSERT INTO dbo.ApplicationSystems
                    (Id, Code, Name, Description, IsActive, CreatedAt, UpdatedAt)
                VALUES
                    ({{applicationId}}, 'MIGRATION_TEST', 'Migration test', NULL, 1, {{now}}, NULL);

                INSERT INTO dbo.ApplicationAccessPolicyRules
                    (Id, ApplicationSystemId, DirectoryGroupId, Name, Priority, Action,
                     MfaRequirement, AllowTrustedDeviceBypass, IncludedIpCidrsJson,
                     ExcludedIpCidrsJson, IsActive, CreatedAt, UpdatedAt)
                VALUES
                    ({{ruleId}}, {{applicationId}}, NULL, 'Existing allow', 100, 'Allow',
                     'Optional', 1, NULL, NULL, 1, {{now}}, NULL);
                """);

            await migrator.MigrateAsync();
            db.ChangeTracker.Clear();

            var migratedRule = await db.ApplicationAccessPolicyRules
                .Include(rule => rule.PolicyVersion)
                .SingleAsync(rule => rule.Id == ruleId);
            Assert.NotEqual(Guid.Empty, migratedRule.PolicyVersionId);
            Assert.Equal(1, migratedRule.PolicyVersion.VersionNumber);
            Assert.Equal(AuthCenter.Domain.Enums.AccessPolicyVersionStatus.Published, migratedRule.PolicyVersion.Status);
            Assert.Equal(AuthCenter.Domain.Enums.AuthenticationAssuranceLevel.Password, migratedRule.RequiredAssuranceLevel);
        }
        finally
        {
            await using var cleanupDb = new AuthCenterDbContext(options);
            await cleanupDb.Database.EnsureDeletedAsync();
        }
    }

    [Fact]
    public async Task RelationalConcurrency_SharedRateLimit_AndDataProtection_WorkAcrossInstances()
    {
        if (!OperatingSystem.IsWindows() && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AUTHCENTER_RELATIONAL_TEST_CONNECTION")))
            return;

        var connectionString = BuildIsolatedConnectionString();
        var options = CreateOptions(connectionString);

        await using (var migrationDb = new AuthCenterDbContext(options))
            await migrationDb.Database.MigrateAsync();

        try
        {
            var userId = Guid.NewGuid();
            var refreshId = Guid.NewGuid();
            var applicationSystemId = Guid.NewGuid();
            var clientId = Guid.NewGuid();
            var authorizationCodeId = Guid.NewGuid();
            var mfaCredentialId = Guid.NewGuid();
            await using (var seedDb = new AuthCenterDbContext(options))
            {
                seedDb.Users.Add(new ApplicationUser
                {
                    Id = userId,
                    UserName = "relational@example.com",
                    NormalizedUserName = "RELATIONAL@EXAMPLE.COM",
                    Email = "relational@example.com",
                    NormalizedEmail = "RELATIONAL@EXAMPLE.COM",
                    FullName = "Relational Test",
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                });
                seedDb.RefreshTokens.Add(new RefreshToken
                {
                    Id = refreshId,
                    UserId = userId,
                    ApplicationCode = "AUTHCENTER",
                    TokenHash = "original",
                    CreatedAt = DateTime.UtcNow,
                    ExpiresAt = DateTime.UtcNow.AddDays(1)
                });
                seedDb.ApplicationSystems.Add(new ApplicationSystem
                {
                    Id = applicationSystemId,
                    Code = "RELATIONAL",
                    Name = "Relational test application",
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                });
                seedDb.OAuthClients.Add(new OAuthClient
                {
                    Id = clientId,
                    ApplicationSystemId = applicationSystemId,
                    ClientId = "relational-client",
                    DisplayName = "Relational client",
                    LoginUrl = "https://client.example.com/login",
                    CreatedAt = DateTime.UtcNow
                });
                seedDb.OAuthAuthorizationCodes.Add(new OAuthAuthorizationCode
                {
                    Id = authorizationCodeId,
                    OAuthClientId = clientId,
                    UserId = userId,
                    CodeHash = "relational-code",
                    RedirectUri = "https://client.example.com/callback",
                    CreatedAt = DateTime.UtcNow,
                    ExpiresAt = DateTime.UtcNow.AddMinutes(5)
                });
                seedDb.UserMfaCredentials.Add(new UserMfaCredential
                {
                    Id = mfaCredentialId,
                    UserId = userId,
                    EncryptedTotpSecret = "test-secret",
                    HashedBackupCodes = "[\"first\",\"second\"]",
                    CreatedAt = DateTime.UtcNow
                });
                await seedDb.SaveChangesAsync();
            }

            await using var firstDb = new AuthCenterDbContext(options);
            await using var secondDb = new AuthCenterDbContext(options);
            var firstToken = await firstDb.RefreshTokens.SingleAsync(token => token.Id == refreshId);
            var secondToken = await secondDb.RefreshTokens.SingleAsync(token => token.Id == refreshId);
            var jwt = Options.Create(new JwtSettings { RefreshTokenDays = 30 });
            var firstService = new RefreshTokenService(firstDb, new DateTimeProvider(), jwt);
            var secondService = new RefreshTokenService(secondDb, new DateTimeProvider(), jwt);

            var rotations = await Task.WhenAll(
                firstService.TryRotateAsync(firstToken, Guid.NewGuid(), "replacement-a", null, null),
                secondService.TryRotateAsync(secondToken, Guid.NewGuid(), "replacement-b", null, null));

            Assert.Single(rotations, result => result);
            await using (var verifyDb = new AuthCenterDbContext(options))
                Assert.Equal(2, await verifyDb.RefreshTokens.CountAsync());

            await using var firstCodeDb = new AuthCenterDbContext(options);
            await using var secondCodeDb = new AuthCenterDbContext(options);
            var firstCode = await firstCodeDb.OAuthAuthorizationCodes.SingleAsync(code => code.Id == authorizationCodeId);
            var secondCode = await secondCodeDb.OAuthAuthorizationCodes.SingleAsync(code => code.Id == authorizationCodeId);
            firstCode.IsUsed = true;
            secondCode.IsUsed = true;
            await firstCodeDb.SaveChangesAsync();
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => secondCodeDb.SaveChangesAsync());

            await using var firstMfaDb = new AuthCenterDbContext(options);
            await using var secondMfaDb = new AuthCenterDbContext(options);
            var firstMfa = await firstMfaDb.UserMfaCredentials.SingleAsync(item => item.Id == mfaCredentialId);
            var secondMfa = await secondMfaDb.UserMfaCredentials.SingleAsync(item => item.Id == mfaCredentialId);
            firstMfa.HashedBackupCodes = "[\"second\"]";
            secondMfa.HashedBackupCodes = "[\"second\"]";
            await firstMfaDb.SaveChangesAsync();
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => secondMfaDb.SaveChangesAsync());

            for (var attempt = 0; attempt < 3; attempt++)
            {
                await using var limiterDb = new AuthCenterDbContext(options);
                var result = await new DistributedRateLimitStore(limiterDb)
                    .TryAcquireAsync("shared-test-bucket", 2, TimeSpan.FromMinutes(1), default);
                Assert.Equal(attempt < 2, result.Acquired);
            }

            const string protectedValue = "multi-instance-secret";
            using (var firstProvider = BuildDataProtectionProvider(connectionString))
            {
                var protectedPayload = firstProvider.GetRequiredService<IDataProtectionProvider>()
                    .CreateProtector("multi-instance-test")
                    .Protect(protectedValue);

                using var secondProvider = BuildDataProtectionProvider(connectionString);
                var unprotected = secondProvider.GetRequiredService<IDataProtectionProvider>()
                    .CreateProtector("multi-instance-test")
                    .Unprotect(protectedPayload);
                Assert.Equal(protectedValue, unprotected);
            }
        }
        finally
        {
            await using var cleanupDb = new AuthCenterDbContext(options);
            await cleanupDb.Database.EnsureDeletedAsync();
        }
    }

    private static DbContextOptions<AuthCenterDbContext> CreateOptions(string connectionString)
    {
        var services = new ServiceCollection();
        services.Configure<Microsoft.AspNetCore.Identity.IdentityOptions>(identity =>
        {
            identity.Stores.SchemaVersion = Microsoft.AspNetCore.Identity.IdentitySchemaVersions.Version3;
            identity.Stores.MaxLengthForKeys = 450;
        });
        var applicationServices = services.BuildServiceProvider();
        return new DbContextOptionsBuilder<AuthCenterDbContext>()
            .UseSqlServer(connectionString)
            .UseApplicationServiceProvider(applicationServices)
            .Options;
    }

    private static ServiceProvider BuildDataProtectionProvider(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddDbContext<AuthCenterDbContext>(options => options.UseSqlServer(connectionString));
        services.AddDataProtection()
            .SetApplicationName("AuthCenter.RelationalTests")
            .PersistKeysToDbContext<AuthCenterDbContext>();
        return services.BuildServiceProvider();
    }

    private static string BuildIsolatedConnectionString()
    {
        var baseConnection = Environment.GetEnvironmentVariable("AUTHCENTER_RELATIONAL_TEST_CONNECTION")
            ?? @"Server=(localdb)\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=true";
        var builder = new SqlConnectionStringBuilder(baseConnection)
        {
            InitialCatalog = $"AuthCenter_Relational_{Guid.NewGuid():N}",
            ConnectTimeout = 30
        };
        return builder.ConnectionString;
    }
}
