using AuthCenter.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthCenter.Infrastructure.Persistence.Configurations;

public sealed class ProvisioningTokenConfiguration : IEntityTypeConfiguration<ProvisioningToken>
{
    public void Configure(EntityTypeBuilder<ProvisioningToken> b) { b.ToTable("ProvisioningTokens"); b.HasKey(x => x.Id); b.Property(x => x.Name).HasMaxLength(150).IsRequired(); b.Property(x => x.TokenHash).HasMaxLength(64).IsRequired(); b.Property(x => x.ScopesJson).HasMaxLength(1000); b.HasIndex(x => x.TokenHash).IsUnique(); b.HasIndex(x => new { x.ApplicationSystemId, x.ExpiresAt }); b.HasOne(x => x.ApplicationSystem).WithMany().HasForeignKey(x => x.ApplicationSystemId).OnDelete(DeleteBehavior.Cascade); }
}
public sealed class ScimRequestLogConfiguration : IEntityTypeConfiguration<ScimRequestLog>
{
    public void Configure(EntityTypeBuilder<ScimRequestLog> b)
    {
        b.ToTable("ScimRequestLogs");
        b.HasKey(x => x.Id);
        b.Property(x => x.Method).HasMaxLength(10);
        b.Property(x => x.Path).HasMaxLength(300);
        b.Property(x => x.ScimType).HasMaxLength(40);
        b.Property(x => x.Detail).HasMaxLength(500);
        b.Property(x => x.TraceId).HasMaxLength(64);
        b.HasIndex(x => new { x.ProvisioningTokenId, x.CreatedAt });
        b.HasIndex(x => x.CreatedAt);
        b.HasOne(x => x.ProvisioningToken).WithMany().HasForeignKey(x => x.ProvisioningTokenId).OnDelete(DeleteBehavior.Cascade);
    }
}
public sealed class ScimResourceLinkConfiguration : IEntityTypeConfiguration<ScimResourceLink>
{
    public void Configure(EntityTypeBuilder<ScimResourceLink> b) { b.ToTable("ScimResourceLinks"); b.HasKey(x => x.Id); b.Property(x => x.ResourceType).HasMaxLength(20); b.Property(x => x.ExternalId).HasMaxLength(300); b.HasIndex(x => new { x.ApplicationSystemId, x.ResourceType, x.ResourceId }).IsUnique(); b.HasIndex(x => new { x.ApplicationSystemId, x.ResourceType, x.ExternalId }).IsUnique().HasFilter("[ExternalId] IS NOT NULL"); b.HasOne(x => x.ApplicationSystem).WithMany().HasForeignKey(x => x.ApplicationSystemId).OnDelete(DeleteBehavior.Cascade); }
}
public sealed class ProfileMappingConfiguration : IEntityTypeConfiguration<ProfileMapping>
{
    public void Configure(EntityTypeBuilder<ProfileMapping> b) { b.ToTable("ProfileMappings"); b.HasKey(x => x.Id); b.Property(x => x.SourceSystem).HasMaxLength(50); b.Property(x => x.SourcePath).HasMaxLength(300); b.Property(x => x.Version).IsConcurrencyToken(); b.HasIndex(x => new { x.ApplicationSystemId, x.SourceSystem, x.SourcePath }).IsUnique(); b.HasOne(x => x.ApplicationSystem).WithMany().HasForeignKey(x => x.ApplicationSystemId).OnDelete(DeleteBehavior.Cascade); b.HasOne(x => x.TargetAttributeDefinition).WithMany().HasForeignKey(x => x.TargetAttributeDefinitionId).OnDelete(DeleteBehavior.Restrict); }
}
public sealed class DynamicGroupRuleConfiguration : IEntityTypeConfiguration<DynamicGroupRule>
{
    public void Configure(EntityTypeBuilder<DynamicGroupRule> b) { b.ToTable("DynamicGroupRules"); b.HasKey(x => x.Id); b.Property(x => x.Operator).HasMaxLength(20); b.Property(x => x.ExpectedValueJson).HasMaxLength(4000); b.Property(x => x.Version).IsConcurrencyToken(); b.HasIndex(x => new { x.DirectoryGroupId, x.ProfileAttributeDefinitionId }).IsUnique(); b.HasOne(x => x.DirectoryGroup).WithMany().HasForeignKey(x => x.DirectoryGroupId).OnDelete(DeleteBehavior.Cascade); b.HasOne(x => x.ProfileAttributeDefinition).WithMany().HasForeignKey(x => x.ProfileAttributeDefinitionId).OnDelete(DeleteBehavior.Restrict); }
}
public sealed class EventHookConfiguration : IEntityTypeConfiguration<EventHook>
{
    public void Configure(EntityTypeBuilder<EventHook> b) { b.ToTable("EventHooks"); b.HasKey(x => x.Id); b.Property(x => x.Name).HasMaxLength(150); b.Property(x => x.Url).HasMaxLength(1000); b.Property(x => x.ProtectedSecret).HasMaxLength(4000); b.Property(x => x.PreviousProtectedSecret).HasMaxLength(4000); b.Property(x => x.EventTypesJson).HasMaxLength(4000); b.Property(x => x.Version).IsConcurrencyToken(); b.HasIndex(x => new { x.ApplicationSystemId, x.Name }).IsUnique(); b.HasOne(x => x.ApplicationSystem).WithMany().HasForeignKey(x => x.ApplicationSystemId).OnDelete(DeleteBehavior.Cascade); }
}
public sealed class EventHookDeliveryConfiguration : IEntityTypeConfiguration<EventHookDelivery>
{
    public void Configure(EntityTypeBuilder<EventHookDelivery> b) { b.ToTable("EventHookDeliveries"); b.HasKey(x => x.Id); b.Property(x => x.EventType).HasMaxLength(150); b.Property(x => x.PayloadJson).HasMaxLength(20000); b.Property(x => x.LastError).HasMaxLength(2000); b.Property(x => x.LastReplayIdempotencyKey).HasMaxLength(100); b.HasIndex(x => new { x.EventHookId, x.EventId }).IsUnique(); b.HasIndex(x => new { x.DeliveredAt, x.DeadLetteredAt, x.NextAttemptAt }); b.HasIndex(x => x.CreatedAt); b.Property(x => x.CreatedAt).HasDefaultValueSql("SYSUTCDATETIME()"); b.HasOne(x => x.EventHook).WithMany(x => x.Deliveries).HasForeignKey(x => x.EventHookId).OnDelete(DeleteBehavior.Cascade); }
}
