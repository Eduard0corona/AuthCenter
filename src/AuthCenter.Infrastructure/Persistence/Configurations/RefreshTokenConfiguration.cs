using AuthCenter.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthCenter.Infrastructure.Persistence.Configurations;

public class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.HasKey(r => r.Id);
        builder.Property(r => r.ApplicationCode).HasMaxLength(50).IsRequired();
        builder.Property(r => r.TokenHash).HasMaxLength(512).IsRequired();
        builder.Property(r => r.ReplacedByTokenHash).HasMaxLength(512);
        builder.Property(r => r.IpAddress).HasMaxLength(45);
        builder.Property(r => r.UserAgent).HasMaxLength(512);
        builder.Property(r => r.CreatedAt).IsRequired();
        builder.Property(r => r.ExpiresAt).IsRequired();
        builder.Property(r => r.RevokedAt).IsConcurrencyToken();

        builder.HasIndex(r => r.TokenHash).IsUnique();
        builder.HasIndex(r => new { r.UserId, r.RevokedAt });
        builder.Property(r => r.OAuthClientId).HasMaxLength(100);
        builder.Property(r => r.GrantedScopes).HasMaxLength(1000);

        builder.Ignore(r => r.IsActive);
    }
}
