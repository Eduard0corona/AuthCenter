using AuthCenter.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthCenter.Infrastructure.Persistence.Configurations;

public class UserTrustedDeviceConfiguration : IEntityTypeConfiguration<UserTrustedDevice>
{
    public void Configure(EntityTypeBuilder<UserTrustedDevice> builder)
    {
        builder.HasKey(d => d.Id);
        builder.HasIndex(d => d.TokenHash).IsUnique();
        builder.HasIndex(d => d.UserId);
        builder.Property(d => d.TokenHash).HasMaxLength(128).IsRequired();
        builder.Property(d => d.DeviceName).HasMaxLength(256);
        builder.Property(d => d.CreatedAt).IsRequired();
        builder.Property(d => d.ExpiresAt).IsRequired();

        builder.HasOne(d => d.User)
            .WithMany()
            .HasForeignKey(d => d.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
