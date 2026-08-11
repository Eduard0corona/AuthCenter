using AuthCenter.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthCenter.Infrastructure.Persistence.Configurations;

public sealed class ApplicationAccessPolicyVersionConfiguration : IEntityTypeConfiguration<ApplicationAccessPolicyVersion>
{
    public void Configure(EntityTypeBuilder<ApplicationAccessPolicyVersion> builder)
    {
        builder.HasKey(version => version.Id);
        builder.Property(version => version.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(version => version.CreatedAt).IsRequired();
        builder.HasAlternateKey(version => new { version.Id, version.ApplicationSystemId });
        builder.HasIndex(version => new { version.ApplicationSystemId, version.VersionNumber }).IsUnique();
        builder.HasIndex(version => new { version.ApplicationSystemId, version.Status });
        builder.HasIndex(version => version.ApplicationSystemId)
            .IsUnique()
            .HasFilter("[Status] = 'Draft'");

        builder.HasOne(version => version.ApplicationSystem)
            .WithMany(application => application.AccessPolicyVersions)
            .HasForeignKey(version => version.ApplicationSystemId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(version => version.CreatedByUser)
            .WithMany()
            .HasForeignKey(version => version.CreatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(version => version.PublishedByUser)
            .WithMany()
            .HasForeignKey(version => version.PublishedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
