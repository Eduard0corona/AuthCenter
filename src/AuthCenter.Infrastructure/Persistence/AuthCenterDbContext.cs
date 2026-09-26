using AuthCenter.Domain.Common;
using System.Text.Json;
using AuthCenter.Domain.Entities;
using AuthCenter.Domain.Events;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;

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
    public DbSet<SingleSignOnSessionClient> SingleSignOnSessionClients => Set<SingleSignOnSessionClient>();
    public DbSet<ApiResource> ApiResources => Set<ApiResource>();
    public DbSet<ApiScope> ApiScopes => Set<ApiScope>();
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
    public DbSet<FederationGroupMapping> FederationGroupMappings => Set<FederationGroupMapping>();
    public DbSet<ProvisioningToken> ProvisioningTokens => Set<ProvisioningToken>();
    public DbSet<ScimResourceLink> ScimResourceLinks => Set<ScimResourceLink>();
    public DbSet<ScimRequestLog> ScimRequestLogs => Set<ScimRequestLog>();
    public DbSet<SamlServiceProvider> SamlServiceProviders => Set<SamlServiceProvider>();
    public DbSet<ApplicationGovernance> ApplicationGovernance => Set<ApplicationGovernance>();
    public DbSet<ApplicationOwner> ApplicationOwners => Set<ApplicationOwner>();
    public DbSet<AccessRequest> AccessRequests => Set<AccessRequest>();
    public DbSet<SeparationOfDutiesRule> SeparationOfDutiesRules => Set<SeparationOfDutiesRule>();
    public DbSet<AccessReviewCampaign> AccessReviewCampaigns => Set<AccessReviewCampaign>();
    public DbSet<AccessReviewItem> AccessReviewItems => Set<AccessReviewItem>();
    public DbSet<ProfileMapping> ProfileMappings => Set<ProfileMapping>();
    public DbSet<DynamicGroupRule> DynamicGroupRules => Set<DynamicGroupRule>();
    public DbSet<EventHook> EventHooks => Set<EventHook>();
    public DbSet<EventHookDelivery> EventHookDeliveries => Set<EventHookDelivery>();
    public DbSet<ApplicationBrandingSettings> ApplicationBrandingSettings => Set<ApplicationBrandingSettings>();
    public DbSet<OAuthConsentGrant> OAuthConsentGrants => Set<OAuthConsentGrant>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        // Identity schema v3 narrows PhoneNumber by default. Preserve the deployed v1 column shape;
        // passkey enablement must not truncate unrelated existing identity data.
        builder.Entity<ApplicationUser>().Property(user => user.PhoneNumber).HasColumnType("nvarchar(max)");
        builder.ApplyConfigurationsFromAssembly(typeof(AuthCenterDbContext).Assembly);

        // Optimistic concurrency for every versioned record: an update whose row changed after it
        // was read affects no row and fails, instead of silently overwriting the newer change.
        foreach (var entityType in builder.Model.GetEntityTypes().Where(type => typeof(IVersionedEntity).IsAssignableFrom(type.ClrType) && type.BaseType is null))
            builder.Entity(entityType.ClrType).Property(nameof(IVersionedEntity.Version)).IsConcurrencyToken();
        builder.Entity<ApplicationUser>().HasQueryFilter(user => user.DeletedAt == null);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        AdvanceVersions();
        AttachTraceId();
        QueueEventHookDeliveriesAsync(CancellationToken.None).GetAwaiter().GetResult();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        AdvanceVersions();
        AttachTraceId();
        await QueueEventHookDeliveriesAsync(cancellationToken);
        return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// Any change to a versioned record moves it to the next version, whichever code path made it,
    /// unless that code already advanced the version itself (after checking the caller's).
    /// </summary>
    private void AdvanceVersions()
    {
        foreach (var entry in ChangeTracker.Entries<IVersionedEntity>())
        {
            if (entry.State != EntityState.Modified)
                continue;
            // Compared by value: Update() marks every property modified, the version included.
            var version = entry.Property(item => item.Version);
            if (version.CurrentValue == version.OriginalValue &&
                entry.Properties.Any(property => property.IsModified && property.Metadata.Name != nameof(IVersionedEntity.Version)))
                entry.Entity.Version++;
        }
    }

    /// <summary>
    /// Every audit event saved through this context, whichever service wrote it, reaches the event
    /// hooks subscribed to its type, in the same transaction as the event. A retried save (execution
    /// strategy) does not queue the same delivery twice.
    /// </summary>
    private async Task QueueEventHookDeliveriesAsync(CancellationToken ct)
    {
        var events = ChangeTracker.Entries<AuditLog>().Where(entry => entry.State == EntityState.Added).Select(entry => entry.Entity).ToList();
        if (events.Count == 0)
            return;
        var hooks = await EventHooks.AsNoTracking()
            .Where(hook => hook.IsActive && hook.IsVerified)
            .Select(hook => new { hook.Id, ApplicationCode = hook.ApplicationSystem != null ? hook.ApplicationSystem.Code : null, hook.EventTypesJson })
            .ToListAsync(ct);
        if (hooks.Count == 0)
            return;
        var queued = ChangeTracker.Entries<EventHookDelivery>()
            .Where(entry => entry.State == EntityState.Added)
            .Select(entry => (entry.Entity.EventHookId, entry.Entity.EventId))
            .ToHashSet();
        foreach (var hook in hooks)
        {
            var types = JsonSerializer.Deserialize<string[]>(hook.EventTypesJson) ?? [];
            foreach (var log in events)
            {
                if (hook.ApplicationCode is not null && !string.Equals(hook.ApplicationCode, log.ApplicationCode, StringComparison.Ordinal))
                    continue;
                if (!types.Contains(log.Action, StringComparer.Ordinal) && !types.Contains(EventTypes.Wildcard, StringComparer.Ordinal))
                    continue;
                if (!queued.Add((hook.Id, log.Id)))
                    continue;
                var occurredAt = log.CreatedAt == default ? DateTime.UtcNow : log.CreatedAt;
                EventHookDeliveries.Add(new EventHookDelivery
                {
                    Id = Guid.NewGuid(),
                    EventHookId = hook.Id,
                    EventId = log.Id,
                    EventType = log.Action,
                    PayloadJson = JsonSerializer.Serialize(new
                    {
                        id = log.Id,
                        type = log.Action,
                        occurredAt,
                        subjectId = log.UserId,
                        applicationCode = log.ApplicationCode,
                        entity = log.EntityName,
                        entityId = log.EntityId,
                        traceId = log.TraceId
                    }),
                    CreatedAt = occurredAt,
                    NextAttemptAt = occurredAt
                });
            }
        }
    }

    private void AttachTraceId()
    {
        var traceId = Activity.Current?.TraceId.ToHexString();
        if (string.IsNullOrEmpty(traceId)) return;
        foreach (var entry in ChangeTracker.Entries<AuditLog>().Where(item => item.State == EntityState.Added && string.IsNullOrEmpty(item.Entity.TraceId)))
            entry.Entity.TraceId = traceId;
    }
}
