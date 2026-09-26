using AuthCenter.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthCenter.Infrastructure.Persistence.Configurations;

public class SingleSignOnSessionClientConfiguration : IEntityTypeConfiguration<SingleSignOnSessionClient>
{
    public void Configure(EntityTypeBuilder<SingleSignOnSessionClient> builder)
    {
        builder.HasKey(item => new { item.SessionId, item.OAuthClientId });
        builder.HasIndex(item => item.UserId);
        builder.HasOne(item => item.OAuthClient)
            .WithMany()
            .HasForeignKey(item => item.OAuthClientId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
