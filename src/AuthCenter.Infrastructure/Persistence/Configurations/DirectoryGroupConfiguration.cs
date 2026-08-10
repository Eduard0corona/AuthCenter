using AuthCenter.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthCenter.Infrastructure.Persistence.Configurations;

public class DirectoryGroupConfiguration : IEntityTypeConfiguration<DirectoryGroup>
{
    public void Configure(EntityTypeBuilder<DirectoryGroup> builder)
    {
        builder.HasKey(group => group.Id);
        builder.Property(group => group.Name).HasMaxLength(200).IsRequired();
        builder.Property(group => group.NormalizedName).HasMaxLength(200).IsRequired();
        builder.Property(group => group.Description).HasMaxLength(1000);
        builder.Property(group => group.CreatedAt).IsRequired();
        builder.HasIndex(group => group.NormalizedName).IsUnique();
        builder.HasIndex(group => new { group.IsActive, group.Name });
    }
}
