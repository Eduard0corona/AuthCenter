using AuthCenter.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthCenter.Infrastructure.Persistence.Configurations;

public class ApplicationSystemConfiguration : IEntityTypeConfiguration<ApplicationSystem>
{
    public void Configure(EntityTypeBuilder<ApplicationSystem> builder)
    {
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Code).HasMaxLength(50).IsRequired();
        builder.Property(a => a.Name).HasMaxLength(200).IsRequired();
        builder.Property(a => a.Description).HasMaxLength(500);
        builder.Property(a => a.CreatedAt).IsRequired();

        builder.HasIndex(a => a.Code).IsUnique();

        builder.HasMany(a => a.Permissions)
            .WithOne(p => p.ApplicationSystem)
            .HasForeignKey(p => p.ApplicationSystemId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(a => a.UserApplicationAccesses)
            .WithOne(u => u.ApplicationSystem)
            .HasForeignKey(u => u.ApplicationSystemId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(a => a.RegistrationSettings)
            .WithOne(r => r.ApplicationSystem)
            .HasForeignKey<ApplicationRegistrationSettings>(r => r.ApplicationSystemId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
