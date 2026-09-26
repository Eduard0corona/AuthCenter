using AuthCenter.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthCenter.Infrastructure.Persistence.Configurations;

public sealed class SamlServiceProviderConfiguration : IEntityTypeConfiguration<SamlServiceProvider>
{
    public void Configure(EntityTypeBuilder<SamlServiceProvider> builder)
    {
        builder.ToTable("SamlServiceProviders");
        builder.HasKey(provider => provider.Id);
        builder.Property(provider => provider.Name).HasMaxLength(150).IsRequired();
        builder.Property(provider => provider.EntityId).HasMaxLength(500).IsRequired();
        builder.HasIndex(provider => provider.EntityId).IsUnique();
        builder.Property(provider => provider.AssertionConsumerServiceUrlsJson).HasMaxLength(8000).IsRequired();
        builder.Property(provider => provider.SingleLogoutServiceUrl).HasMaxLength(1000);
        builder.Property(provider => provider.NameIdFormat).HasMaxLength(200).IsRequired();
        builder.Property(provider => provider.NameIdSalt).HasMaxLength(100).IsRequired();
        builder.Property(provider => provider.SigningCertificate).HasMaxLength(8000);
        builder.Property(provider => provider.EncryptionCertificate).HasMaxLength(8000);
        builder.Property(provider => provider.AttributesJson).HasMaxLength(8000).IsRequired();
        builder.Property(provider => provider.DefaultRelayState).HasMaxLength(500);
        builder.Property(provider => provider.Version).IsConcurrencyToken();
        builder.HasIndex(provider => provider.ApplicationSystemId);
        builder.HasOne(provider => provider.ApplicationSystem).WithMany().HasForeignKey(provider => provider.ApplicationSystemId).OnDelete(DeleteBehavior.Restrict);
    }
}
