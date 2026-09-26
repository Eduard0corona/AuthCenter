using AuthCenter.Api.Services;
using AuthCenter.Domain.Entities;
using AuthCenter.Domain.Constants;
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
    [RelationalFact]
    public async Task IdempotentDeploymentScript_UpgradesProductionBaseline()
    {
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
            Assert.Equal(verifyDb.Database.GetMigrations().Count(), applied.Count);
            Assert.Contains("20260811070000_CompleteOktaPhase1", applied);
            Assert.Contains("20260811091450_AddIdentityPasskeysPhase2", applied);
            Assert.Contains("20260811092337_CompleteAdaptiveAuthenticationPhase2", applied);
            Assert.Contains("20260811093652_CompleteEnterpriseFederationPhase3", applied);
            Assert.Contains("20260811095011_CompleteLifecycleAutomationPhase4", applied);
            Assert.Contains("20260811104737_CompleteDeveloperExperiencePhase5", applied);
            Assert.Contains("20260811105540_CompleteOperationalExcellencePhase6", applied);
            Assert.Contains("20260811120003_EnsureFirstPartyApplicationAvailability", applied);
            Assert.Contains(applied, migration => migration.EndsWith("_CompleteAdminBackendContracts", StringComparison.Ordinal));
            Assert.Contains("20260926092052_AddSingleSignOnSessionContext", applied);

            var firstPartyApplication = await verifyDb.ApplicationSystems
                .Include(application => application.RegistrationSettings)
                .Include(application => application.BrandingSettings)
                .SingleAsync(application => application.Code == "AUTHCENTER");
            Assert.True(firstPartyApplication.IsActive);
            Assert.NotNull(firstPartyApplication.RegistrationSettings);
            Assert.NotNull(firstPartyApplication.BrandingSettings);
            Assert.Equal("AuthCenter", firstPartyApplication.BrandingSettings.DisplayName);
        }
        finally
        {
            await using var cleanupDb = new AuthCenterDbContext(options);
            await cleanupDb.Database.EnsureDeletedAsync();
        }
    }

    [RelationalFact]
    public async Task Phase1Migration_PreservesExistingRulesAsPublishedVersion()
    {
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

    [RelationalFact]
    public async Task OperationsPermissionsMigration_GrantsNewPermissionsToRolesHoldingLegacyOnes()
    {
        var connectionString = BuildIsolatedConnectionString();
        var options = CreateOptions(connectionString);
        try
        {
            await using var db = new AuthCenterDbContext(options);
            var migrator = db.Database.GetService<IMigrator>();
            await migrator.MigrateAsync("20260926131814_AddEventHookSecretRotation");

            var readerRoleId = Guid.NewGuid();
            var writerRoleId = Guid.NewGuid();
            var now = DateTime.UtcNow;
            await db.Database.ExecuteSqlInterpolatedAsync($$"""
                DECLARE @applicationId uniqueidentifier = (SELECT Id FROM dbo.ApplicationSystems WHERE Code = 'AUTHCENTER');
                INSERT INTO dbo.Permissions (Id, ApplicationSystemId, Code, Name, Description, IsActive, CreatedAt)
                SELECT NEWID(), @applicationId, legacy.Code, legacy.Code, NULL, 1, {{now}}
                FROM (VALUES ('AUTHCENTER_APPLICATIONS_READ'), ('AUTHCENTER_APPLICATIONS_WRITE')) legacy(Code)
                WHERE NOT EXISTS (SELECT 1 FROM dbo.Permissions p WHERE p.ApplicationSystemId = @applicationId AND p.Code = legacy.Code);

                INSERT INTO dbo.AspNetRoles (Id, ApplicationSystemId, Name, NormalizedName, DisplayName, IsActive, IsSystemRole, CreatedAt)
                VALUES ({{readerRoleId}}, @applicationId, 'Migration reader', 'MIGRATION READER', 'Migration reader', 1, 0, {{now}}),
                       ({{writerRoleId}}, @applicationId, 'Migration writer', 'MIGRATION WRITER', 'Migration writer', 1, 0, {{now}});

                INSERT INTO dbo.RolePermissions (RoleId, PermissionId, CreatedAt)
                SELECT {{readerRoleId}}, Id, {{now}} FROM dbo.Permissions WHERE ApplicationSystemId = @applicationId AND Code = 'AUTHCENTER_APPLICATIONS_READ'
                UNION ALL
                SELECT {{writerRoleId}}, Id, {{now}} FROM dbo.Permissions WHERE ApplicationSystemId = @applicationId AND Code IN ('AUTHCENTER_APPLICATIONS_READ', 'AUTHCENTER_APPLICATIONS_WRITE');
                """);

            await migrator.MigrateAsync();
            db.ChangeTracker.Clear();

            async Task<string[]> GrantedCodesAsync(Guid roleId) => await db.RolePermissions
                .Where(grant => grant.RoleId == roleId)
                .Select(grant => grant.Permission.Code)
                .OrderBy(code => code)
                .ToArrayAsync();

            Assert.Equal(
                [
                    DomainConstants.Permissions.ApplicationsRead,
                    DomainConstants.Permissions.EventHooksRead,
                    DomainConstants.Permissions.FederationRead,
                    DomainConstants.Permissions.ProvisioningRead
                ],
                await GrantedCodesAsync(readerRoleId));
            Assert.Equal(
                [
                    DomainConstants.Permissions.ApplicationsRead,
                    DomainConstants.Permissions.ApplicationsWrite,
                    DomainConstants.Permissions.EventHooksRead,
                    DomainConstants.Permissions.EventHooksWrite,
                    DomainConstants.Permissions.FederationRead,
                    DomainConstants.Permissions.FederationWrite,
                    DomainConstants.Permissions.ProvisioningRead,
                    DomainConstants.Permissions.ProvisioningWrite
                ],
                await GrantedCodesAsync(writerRoleId));
        }
        finally
        {
            await using var cleanupDb = new AuthCenterDbContext(options);
            await cleanupDb.Database.EnsureDeletedAsync();
        }
    }

    [RelationalFact]
    public async Task VersionedRecords_RefuseTheSecondOfTwoConcurrentSaves()
    {
        var connectionString = BuildIsolatedConnectionString();
        var options = CreateOptions(connectionString);
        await using (var migrationDb = new AuthCenterDbContext(options))
            await migrationDb.Database.MigrateAsync();
        try
        {
            await using var first = new AuthCenterDbContext(options);
            await using var second = new AuthCenterDbContext(options);
            var fromFirst = await first.ApplicationSystems.SingleAsync(application => application.Code == "AUTHCENTER");
            var fromSecond = await second.ApplicationSystems.SingleAsync(application => application.Code == "AUTHCENTER");

            fromFirst.Description = "Saved first";
            await first.SaveChangesAsync();
            fromSecond.Description = "Saved second";

            // Any change advances the version, and the version is checked by the UPDATE itself.
            Assert.Equal(fromSecond.Version + 1, fromFirst.Version);
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
            await using var verify = new AuthCenterDbContext(options);
            Assert.Equal("Saved first", (await verify.ApplicationSystems.SingleAsync(application => application.Code == "AUTHCENTER")).Description);
        }
        finally
        {
            await using var cleanupDb = new AuthCenterDbContext(options);
            await cleanupDb.Database.EnsureDeletedAsync();
        }
    }

    [RelationalFact]
    public async Task RelationalConcurrency_SharedRateLimit_AndDataProtection_WorkAcrossInstances()
    {
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
            var firstService = new RefreshTokenService(firstDb, new DateTimeProvider(), new BackchannelLogoutQueue(firstDb, new EphemeralDataProtectionProvider(), new DateTimeProvider()), jwt);
            var secondService = new RefreshTokenService(secondDb, new DateTimeProvider(), new BackchannelLogoutQueue(secondDb, new EphemeralDataProtectionProvider(), new DateTimeProvider()), jwt);

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

    [RelationalFact]
    public async Task SuperAdminApplicationLock_SerializesConcurrentRemovalAttempts()
    {
        var connectionString = BuildIsolatedConnectionString(); var options = CreateOptions(connectionString);
        try
        {
            await using (var setup = new AuthCenterDbContext(options))
            {
                await setup.Database.MigrateAsync(); var now = DateTime.UtcNow; var appId = await setup.ApplicationSystems.Where(x => x.Code == DomainConstants.SystemCodes.AuthCenter).Select(x => x.Id).SingleAsync(); var roleId = Guid.NewGuid();
                setup.Roles.Add(new ApplicationRole { Id = roleId, Name = "AUTHCENTER:SuperAdmin", NormalizedName = "AUTHCENTER:SUPERADMIN", DisplayName = DomainConstants.Roles.SuperAdmin, ApplicationSystemId = appId, IsSystemRole = true, IsActive = true, CreatedAt = now });
                foreach (var userId in new[] { Guid.NewGuid(), Guid.NewGuid() })
                {
                    setup.Users.Add(new ApplicationUser { Id = userId, Email = $"{userId:N}@example.com", NormalizedEmail = $"{userId:N}@EXAMPLE.COM", UserName = $"{userId:N}@example.com", NormalizedUserName = $"{userId:N}@EXAMPLE.COM", FullName = "Concurrent admin", IsActive = true, EmailConfirmed = true, CreatedAt = now, SecurityStamp = Guid.NewGuid().ToString("N"), ConcurrencyStamp = Guid.NewGuid().ToString("N") });
                    setup.UserRoles.Add(new Microsoft.AspNetCore.Identity.IdentityUserRole<Guid> { UserId = userId, RoleId = roleId });
                    setup.UserApplicationAccesses.Add(new UserApplicationAccess { Id = Guid.NewGuid(), UserId = userId, ApplicationSystemId = appId, IsActive = true, CreatedAt = now });
                }
                await setup.SaveChangesAsync();
            }
            Guid[] targets; Guid app; Guid role;
            await using (var lookup = new AuthCenterDbContext(options)) { targets = await lookup.Users.Select(x => x.Id).ToArrayAsync(); app = await lookup.ApplicationSystems.Select(x => x.Id).SingleAsync(); role = await lookup.Roles.Select(x => x.Id).SingleAsync(); }
            async Task<bool> TryDeactivateAsync(Guid target)
            {
                await using var db = new AuthCenterDbContext(options); await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
                await SuperAdminInvariantLock.AcquireAsync(db);
                var effective = await db.Users.CountAsync(user => user.IsActive && db.UserRoles.Any(x => x.UserId == user.Id && x.RoleId == role) && db.UserApplicationAccesses.Any(x => x.UserId == user.Id && x.ApplicationSystemId == app && x.IsActive && x.RevokedAt == null));
                if (effective <= 1) { await transaction.RollbackAsync(); return false; }
                var user = await db.Users.SingleAsync(x => x.Id == target); user.IsActive = false; await db.SaveChangesAsync(); await transaction.CommitAsync(); return true;
            }
            var results = await Task.WhenAll(targets.Select(TryDeactivateAsync)); Assert.Single(results, x => x); Assert.Single(results, x => !x);
            await using var verify = new AuthCenterDbContext(options); Assert.Equal(1, await verify.Users.CountAsync(x => x.IsActive));
        }
        finally { await using var cleanup = new AuthCenterDbContext(options); await cleanup.Database.EnsureDeletedAsync(); }
    }

    internal static DbContextOptions<AuthCenterDbContext> CreateOptions(string connectionString)
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

    internal static string BuildIsolatedConnectionString()
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
