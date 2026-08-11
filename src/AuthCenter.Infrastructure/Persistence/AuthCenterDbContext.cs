using AuthCenter.Domain.Entities;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace AuthCenter.Infrastructure.Persistence;

public class AuthCenterDbContext : IdentityDbContext<ApplicationUser, ApplicationRole, Guid>, IDataProtectionKeyContext
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
    public DbSet<TransientState> TransientStates => Set<TransientState>();
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();
    public DbSet<DistributedRateLimitBucket> DistributedRateLimitBuckets => Set<DistributedRateLimitBucket>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<DirectoryGroup> DirectoryGroups => Set<DirectoryGroup>();
    public DbSet<UserGroupMembership> UserGroupMemberships => Set<UserGroupMembership>();
    public DbSet<GroupApplicationAssignment> GroupApplicationAssignments => Set<GroupApplicationAssignment>();
    public DbSet<GroupRoleAssignment> GroupRoleAssignments => Set<GroupRoleAssignment>();
    public DbSet<ApplicationAccessPolicyRule> ApplicationAccessPolicyRules => Set<ApplicationAccessPolicyRule>();
    public DbSet<ApplicationAccessPolicyVersion> ApplicationAccessPolicyVersions => Set<ApplicationAccessPolicyVersion>();
    public DbSet<UserProfileAttributeDefinition> UserProfileAttributeDefinitions => Set<UserProfileAttributeDefinition>();
    public DbSet<UserProfileAttributeValue> UserProfileAttributeValues => Set<UserProfileAttributeValue>();
    public DbSet<AuthenticationObservation> AuthenticationObservations => Set<AuthenticationObservation>();
    public DbSet<FederationProvider> FederationProviders => Set<FederationProvider>();
    public DbSet<FederationRoutingRule> FederationRoutingRules => Set<FederationRoutingRule>();
    public DbSet<ProvisioningToken> ProvisioningTokens => Set<ProvisioningToken>();
    public DbSet<ScimResourceLink> ScimResourceLinks => Set<ScimResourceLink>();
    public DbSet<ProfileMapping> ProfileMappings => Set<ProfileMapping>();
    public DbSet<DynamicGroupRule> DynamicGroupRules => Set<DynamicGroupRule>();
    public DbSet<EventHook> EventHooks => Set<EventHook>();
    public DbSet<EventHookDelivery> EventHookDeliveries => Set<EventHookDelivery>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        // Identity schema v3 narrows PhoneNumber by default. Preserve the deployed v1 column shape;
        // passkey enablement must not truncate unrelated existing identity data.
        builder.Entity<ApplicationUser>().Property(user => user.PhoneNumber).HasColumnType("nvarchar(max)");
        builder.ApplyConfigurationsFromAssembly(typeof(AuthCenterDbContext).Assembly);
        builder.Entity<ApplicationUser>().HasQueryFilter(user => user.DeletedAt == null);
    }
}
