using AuthCenter.Domain.Constants;
using AuthCenter.Domain.Entities;
using AuthCenter.Domain.Enums;
using AuthCenter.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AuthCenter.Infrastructure.Persistence.Seed;

public static class AuthCenterSeeder
{
    public static async Task SeedAsync(
        AuthCenterDbContext db,
        UserManager<ApplicationUser> userManager,
        RoleManager<ApplicationRole> roleManager,
        IConfiguration configuration,
        ILogger logger)
    {
        await db.Database.EnsureCreatedAsync();

        var appSystem = await db.ApplicationSystems
            .Include(a => a.RegistrationSettings)
            .FirstOrDefaultAsync(a => a.Code == DomainConstants.SystemCodes.AuthCenter);

        if (appSystem is null)
        {
            var now = DateTime.UtcNow;
            appSystem = new ApplicationSystem
            {
                Id = Guid.NewGuid(),
                Code = DomainConstants.SystemCodes.AuthCenter,
                Name = "AuthCenter",
                Description = "Centralized authentication and identity service",
                IsActive = true,
                CreatedAt = now,
                RegistrationSettings = new ApplicationRegistrationSettings
                {
                    Id = Guid.NewGuid(),
                    RegistrationMode = ApplicationRegistrationMode.InviteOnly,
                    AllowGoogleLogin = true,
                    AllowPasswordLogin = true,
                    RequireEmailConfirmation = false,
                    CreatedAt = now
                }
            };
            db.ApplicationSystems.Add(appSystem);
            await db.SaveChangesAsync();
            logger.LogInformation("Created ApplicationSystem AUTHCENTER");
        }

        var permissionCodes = new[]
        {
            (DomainConstants.Permissions.UsersRead, "Read users"),
            (DomainConstants.Permissions.UsersWrite, "Write users"),
            (DomainConstants.Permissions.ApplicationsRead, "Read applications"),
            (DomainConstants.Permissions.ApplicationsWrite, "Write applications"),
            (DomainConstants.Permissions.RolesRead, "Read roles"),
            (DomainConstants.Permissions.RolesWrite, "Write roles"),
            (DomainConstants.Permissions.PermissionsRead, "Read permissions"),
            (DomainConstants.Permissions.PermissionsWrite, "Write permissions"),
            (DomainConstants.Permissions.AuditLogsRead, "Read audit logs"),
        };

        var now2 = DateTime.UtcNow;
        foreach (var (code, name) in permissionCodes)
        {
            if (!await db.Permissions.AnyAsync(p => p.Code == code && p.ApplicationSystemId == appSystem.Id))
            {
                db.Permissions.Add(new Permission
                {
                    Id = Guid.NewGuid(),
                    ApplicationSystemId = appSystem.Id,
                    Code = code,
                    Name = name,
                    IsActive = true,
                    CreatedAt = now2
                });
            }
        }
        await db.SaveChangesAsync();

        await EnsureRoleAsync(roleManager, db, DomainConstants.Roles.SuperAdmin, "Super administrator with full access", appSystem.Id, isSystem: true, permissionCodes.Select(p => p.Item1).ToArray(), logger);
        await EnsureRoleAsync(roleManager, db, DomainConstants.Roles.Admin, "Administrator with management access", appSystem.Id, isSystem: true,
            [
                DomainConstants.Permissions.UsersRead,
                DomainConstants.Permissions.UsersWrite,
                DomainConstants.Permissions.ApplicationsRead,
                DomainConstants.Permissions.RolesRead,
                DomainConstants.Permissions.PermissionsRead,
                DomainConstants.Permissions.AuditLogsRead,
            ], logger);

        await SeedAdminUserAsync(userManager, db, appSystem, configuration, logger);
    }

    private static async Task EnsureRoleAsync(
        RoleManager<ApplicationRole> roleManager,
        AuthCenterDbContext db,
        string roleName,
        string description,
        Guid appSystemId,
        bool isSystem,
        string[] permissionCodes,
        ILogger logger)
    {
        if (!await roleManager.RoleExistsAsync(roleName))
        {
            var role = new ApplicationRole
            {
                Id = Guid.NewGuid(),
                Name = roleName,
                NormalizedName = roleName.ToUpperInvariant(),
                Description = description,
                ApplicationSystemId = appSystemId,
                IsSystemRole = isSystem,
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            };
            await roleManager.CreateAsync(role);
            logger.LogInformation("Created role {RoleName}", roleName);

            var now = DateTime.UtcNow;
            foreach (var code in permissionCodes)
            {
                var perm = await db.Permissions.FirstOrDefaultAsync(p => p.Code == code);
                if (perm is not null && !await db.RolePermissions.AnyAsync(rp => rp.RoleId == role.Id && rp.PermissionId == perm.Id))
                {
                    db.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = perm.Id, CreatedAt = now });
                }
            }
            await db.SaveChangesAsync();
        }
    }

    private static async Task SeedAdminUserAsync(
        UserManager<ApplicationUser> userManager,
        AuthCenterDbContext db,
        ApplicationSystem appSystem,
        IConfiguration configuration,
        ILogger logger)
    {
        var email = configuration["Seed:AdminEmail"];
        var password = configuration["Seed:AdminPassword"];
        var fullName = configuration["Seed:AdminFullName"];

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(fullName))
        {
            logger.LogWarning("Seed:AdminEmail, Seed:AdminPassword, or Seed:AdminFullName not configured — skipping admin user creation");
            return;
        }

        if (await userManager.FindByEmailAsync(email) is not null)
            return;

        var now = DateTime.UtcNow;
        var admin = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            FullName = fullName,
            Email = email,
            UserName = email,
            EmailConfirmed = true,
            IsExternalUser = false,
            HasLocalPassword = true,
            IsActive = true,
            CreatedAt = now
        };

        var result = await userManager.CreateAsync(admin, password);
        if (!result.Succeeded)
        {
            logger.LogError("Failed to create admin user: {Errors}", string.Join(", ", result.Errors.Select(e => e.Description)));
            return;
        }

        await userManager.AddToRoleAsync(admin, DomainConstants.Roles.SuperAdmin);

        db.UserApplicationAccesses.Add(new UserApplicationAccess
        {
            Id = Guid.NewGuid(),
            UserId = admin.Id,
            ApplicationSystemId = appSystem.Id,
            IsActive = true,
            CreatedAt = now
        });
        await db.SaveChangesAsync();

        logger.LogInformation("Admin user {Email} created and assigned SuperAdmin role", email);
    }
}
