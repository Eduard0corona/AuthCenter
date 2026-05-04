using AuthCenter.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthCenter.Infrastructure.Persistence.Configurations;

public class UserMfaCredentialConfiguration : IEntityTypeConfiguration<UserMfaCredential>
{
    public void Configure(EntityTypeBuilder<UserMfaCredential> builder)
    {
        builder.HasKey(m => m.Id);
        builder.HasIndex(m => m.UserId).IsUnique();
        builder.Property(m => m.EncryptedTotpSecret).HasMaxLength(512).IsRequired();
        builder.Property(m => m.HashedBackupCodes).HasMaxLength(2048);
        builder.Property(m => m.CreatedAt).IsRequired();
    }
}
