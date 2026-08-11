using AuthCenter.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthCenter.Infrastructure.Persistence.Configurations;

public sealed class AuthenticationObservationConfiguration : IEntityTypeConfiguration<AuthenticationObservation>
{
    public void Configure(EntityTypeBuilder<AuthenticationObservation> builder)
    {
        builder.ToTable("AuthenticationObservations");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.NetworkHash).HasMaxLength(64).IsRequired();
        builder.Property(item => item.DeviceHash).HasMaxLength(64).IsRequired();
        builder.Property(item => item.RiskLevel).HasConversion<string>().HasMaxLength(20);
        builder.Property(item => item.ReasonCodesJson).HasMaxLength(1000);
        builder.Property(item => item.Latitude).HasPrecision(6, 2);
        builder.Property(item => item.Longitude).HasPrecision(6, 2);
        builder.HasIndex(item => new { item.UserId, item.ObservedAt });
        builder.HasIndex(item => item.ExpiresAt);
        builder.HasOne(item => item.User).WithMany().HasForeignKey(item => item.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
