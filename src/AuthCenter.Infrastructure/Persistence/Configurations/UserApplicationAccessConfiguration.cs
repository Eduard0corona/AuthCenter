using AuthCenter.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthCenter.Infrastructure.Persistence.Configurations;

public class UserApplicationAccessConfiguration : IEntityTypeConfiguration<UserApplicationAccess>
{
    public void Configure(EntityTypeBuilder<UserApplicationAccess> builder)
    {
        builder.HasKey(u => u.Id);
        builder.Property(u => u.CreatedAt).IsRequired();

        builder.HasIndex(u => new { u.UserId, u.ApplicationSystemId }).IsUnique();
    }
}
