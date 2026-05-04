using AuthCenter.Domain.Entities;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace AuthCenter.Infrastructure.Persistence;

public class AuthCenterDbContext : IdentityDbContext<ApplicationUser, ApplicationRole, Guid>
{
    public AuthCenterDbContext(DbContextOptions<AuthCenterDbContext> options) : base(options) { }

    public DbSet<ApplicationSystem> ApplicationSystems => Set<ApplicationSystem>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<UserApplicationAccess> UserApplicationAccesses => Set<UserApplicationAccess>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<ExternalIdentityProvider> ExternalIdentityProviders => Set<ExternalIdentityProvider>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<ApplicationRegistrationSettings> ApplicationRegistrationSettings => Set<ApplicationRegistrationSettings>();
    public DbSet<UserMfaCredential> UserMfaCredentials => Set<UserMfaCredential>();
    public DbSet<UserTrustedDevice> UserTrustedDevices => Set<UserTrustedDevice>();
    public DbSet<OAuthClient> OAuthClients => Set<OAuthClient>();
    public DbSet<OAuthAuthorizationCode> OAuthAuthorizationCodes => Set<OAuthAuthorizationCode>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfigurationsFromAssembly(typeof(AuthCenterDbContext).Assembly);
    }
}
