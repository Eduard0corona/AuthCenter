using AuthCenter.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthCenter.Infrastructure.Persistence.Configurations;

public sealed class FederationProviderConfiguration : IEntityTypeConfiguration<FederationProvider>
{
    public void Configure(EntityTypeBuilder<FederationProvider> builder)
    {
        builder.ToTable("FederationProviders");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Name).HasMaxLength(150).IsRequired();
        builder.Property(item => item.Protocol).HasConversion<string>().HasMaxLength(20);
        builder.Property(item => item.Issuer).HasMaxLength(500).IsRequired();
        builder.Property(item => item.DiscoveryEndpoint).HasMaxLength(1000);
        builder.Property(item => item.ClientId).HasMaxLength(300);
        builder.Property(item => item.OidcCallbackUrl).HasMaxLength(1000);
        builder.Property(item => item.ProtectedClientSecret).HasMaxLength(4000);
        builder.Property(item => item.SamlSingleSignOnUrl).HasMaxLength(1000);
        builder.Property(item => item.SamlSigningCertificatePem).HasMaxLength(10000);
        builder.Property(item => item.AccountLinkingMode).HasConversion<string>().HasMaxLength(30);
        builder.Property(item => item.GroupsClaim).HasMaxLength(256);
        builder.Property(item => item.Version).IsConcurrencyToken();
        builder.HasIndex(item => new { item.ApplicationSystemId, item.Name }).IsUnique();
        builder.HasOne(item => item.ApplicationSystem).WithMany().HasForeignKey(item => item.ApplicationSystemId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class FederationRoutingRuleConfiguration : IEntityTypeConfiguration<FederationRoutingRule>
{
    public void Configure(EntityTypeBuilder<FederationRoutingRule> builder)
    {
        builder.ToTable("FederationRoutingRules");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.EmailDomain).HasMaxLength(255);
        builder.Property(item => item.ExpectedProfileValueJson).HasMaxLength(4000);
        builder.Property(item => item.Version).IsConcurrencyToken();
        builder.HasIndex(item => new { item.FederationProviderId, item.Priority }).IsUnique();
        builder.HasOne(item => item.FederationProvider).WithMany(item => item.RoutingRules).HasForeignKey(item => item.FederationProviderId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(item => item.DirectoryGroup).WithMany().HasForeignKey(item => item.DirectoryGroupId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(item => item.ProfileAttributeDefinition).WithMany().HasForeignKey(item => item.ProfileAttributeDefinitionId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class FederationGroupMappingConfiguration : IEntityTypeConfiguration<FederationGroupMapping>
{
    public void Configure(EntityTypeBuilder<FederationGroupMapping> builder)
    {
        builder.ToTable("FederationGroupMappings");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.UpstreamValue).HasMaxLength(256).IsRequired();
        builder.HasIndex(item => new { item.FederationProviderId, item.UpstreamValue, item.DirectoryGroupId }).IsUnique();
        builder.HasOne(item => item.FederationProvider).WithMany(item => item.GroupMappings).HasForeignKey(item => item.FederationProviderId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(item => item.DirectoryGroup).WithMany().HasForeignKey(item => item.DirectoryGroupId).OnDelete(DeleteBehavior.Restrict);
    }
}
