using AuthCenter.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthCenter.Infrastructure.Persistence.Configurations;

public sealed class ApplicationBrandingSettingsConfiguration : IEntityTypeConfiguration<ApplicationBrandingSettings>
{
    public void Configure(EntityTypeBuilder<ApplicationBrandingSettings> builder)
    {
        builder.HasKey(item => item.Id);
        builder.HasIndex(item => item.ApplicationSystemId).IsUnique();
        builder.Property(item => item.DisplayName).HasMaxLength(100).IsRequired();
        builder.Property(item => item.PrimaryColor).HasMaxLength(7).IsRequired();
        builder.Property(item => item.BackgroundColor).HasMaxLength(7).IsRequired();
        builder.Property(item => item.LogoUrl).HasMaxLength(500);
        builder.Property(item => item.SupportUrl).HasMaxLength(500);
        builder.Property(item => item.PrivacyUrl).HasMaxLength(500);
        builder.Property(item => item.TermsUrl).HasMaxLength(500);
        builder.HasOne(item => item.ApplicationSystem)
            .WithOne(item => item.BrandingSettings)
            .HasForeignKey<ApplicationBrandingSettings>(item => item.ApplicationSystemId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class OAuthConsentGrantConfiguration : IEntityTypeConfiguration<OAuthConsentGrant>
{
    public void Configure(EntityTypeBuilder<OAuthConsentGrant> builder)
    {
        builder.HasKey(item => item.Id);
        builder.HasIndex(item => new { item.UserId, item.OAuthClientId }).IsUnique();
        builder.Property(item => item.ScopesJson).HasMaxLength(1000).IsRequired();
        builder.HasOne(item => item.User)
            .WithMany()
            .HasForeignKey(item => item.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(item => item.OAuthClient)
            .WithMany(item => item.ConsentGrants)
            .HasForeignKey(item => item.OAuthClientId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
