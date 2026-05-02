using AuthCenter.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthCenter.Infrastructure.Persistence.Configurations;

public class ExternalIdentityProviderConfiguration : IEntityTypeConfiguration<ExternalIdentityProvider>
{
    public void Configure(EntityTypeBuilder<ExternalIdentityProvider> builder)
    {
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Provider).HasMaxLength(50).IsRequired();
        builder.Property(e => e.ProviderUserId).HasMaxLength(256).IsRequired();
        builder.Property(e => e.Email).HasMaxLength(256).IsRequired();
        builder.Property(e => e.DisplayName).HasMaxLength(200);
        builder.Property(e => e.PictureUrl).HasMaxLength(2048);
        builder.Property(e => e.LinkedAt).IsRequired();

        builder.HasIndex(e => new { e.Provider, e.ProviderUserId }).IsUnique();
    }
}
