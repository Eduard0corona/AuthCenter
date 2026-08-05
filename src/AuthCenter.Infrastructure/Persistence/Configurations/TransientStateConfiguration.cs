using AuthCenter.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthCenter.Infrastructure.Persistence.Configurations;

public class TransientStateConfiguration : IEntityTypeConfiguration<TransientState>
{
    public void Configure(EntityTypeBuilder<TransientState> builder)
    {
        builder.HasKey(s => s.Id);

        builder.Property(s => s.Purpose).HasMaxLength(50).IsRequired();
        builder.Property(s => s.Key).HasMaxLength(200).IsRequired();
        builder.Property(s => s.CreatedAt).IsRequired();
        builder.Property(s => s.ExpiresAt).IsRequired();

        // What actually makes single use atomic across instances: two racing redemptions of the
        // same token cannot both insert.
        builder.HasIndex(s => new { s.Purpose, s.Key }).IsUnique();

        // Supports the sweep that drops expired rows.
        builder.HasIndex(s => s.ExpiresAt);
    }
}
