using AuthCenter.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthCenter.Infrastructure.Persistence.Configurations;

public class OAuthClientConfiguration : IEntityTypeConfiguration<OAuthClient>
{
    public void Configure(EntityTypeBuilder<OAuthClient> builder)
    {
        builder.HasKey(c => c.Id);
        builder.Property(c => c.ClientId).HasMaxLength(100).IsRequired();
        builder.Property(c => c.HashedClientSecret).HasMaxLength(256);
        builder.Property(c => c.DisplayName).HasMaxLength(200).IsRequired();
        builder.Property(c => c.RedirectUrisJson).HasMaxLength(4000).IsRequired();
        builder.Property(c => c.AllowedScopesJson).HasMaxLength(1000).IsRequired();
        builder.Property(c => c.GrantTypesJson).HasMaxLength(500).IsRequired();
        builder.Property(c => c.LoginUrl).HasMaxLength(500).IsRequired();
        builder.Property(c => c.PostLogoutRedirectUrisJson).HasMaxLength(4000).IsRequired().HasDefaultValue("[]");
        builder.Property(c => c.BackchannelLogoutUri).HasMaxLength(500);
        builder.Property(c => c.CreatedAt).IsRequired();

        builder.HasIndex(c => c.ClientId).IsUnique();
        builder.HasIndex(c => c.ApplicationSystemId);

        builder.HasOne(c => c.ApplicationSystem)
            .WithMany(a => a.OAuthClients)
            .HasForeignKey(c => c.ApplicationSystemId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(c => c.AuthorizationCodes)
            .WithOne(a => a.OAuthClient)
            .HasForeignKey(a => a.OAuthClientId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
