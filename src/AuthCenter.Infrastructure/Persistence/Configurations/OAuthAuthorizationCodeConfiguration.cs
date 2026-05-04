using AuthCenter.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthCenter.Infrastructure.Persistence.Configurations;

public class OAuthAuthorizationCodeConfiguration : IEntityTypeConfiguration<OAuthAuthorizationCode>
{
    public void Configure(EntityTypeBuilder<OAuthAuthorizationCode> builder)
    {
        builder.HasKey(c => c.Id);
        builder.Property(c => c.CodeHash).HasMaxLength(256).IsRequired();
        builder.Property(c => c.RedirectUri).HasMaxLength(500).IsRequired();
        builder.Property(c => c.ScopesJson).HasMaxLength(1000).IsRequired();
        builder.Property(c => c.CodeChallenge).HasMaxLength(256);
        builder.Property(c => c.CodeChallengeMethod).HasMaxLength(10);
        builder.Property(c => c.Nonce).HasMaxLength(256);
        builder.Property(c => c.CreatedAt).IsRequired();
        builder.Property(c => c.ExpiresAt).IsRequired();

        builder.HasIndex(c => c.CodeHash).IsUnique();
        builder.HasIndex(c => c.OAuthClientId);
        builder.HasIndex(c => c.UserId);

        builder.HasOne(c => c.User)
            .WithMany()
            .HasForeignKey(c => c.UserId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
