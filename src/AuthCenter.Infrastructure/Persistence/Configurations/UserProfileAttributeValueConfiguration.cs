using AuthCenter.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthCenter.Infrastructure.Persistence.Configurations;

public sealed class UserProfileAttributeValueConfiguration : IEntityTypeConfiguration<UserProfileAttributeValue>
{
    public void Configure(EntityTypeBuilder<UserProfileAttributeValue> builder)
    {
        builder.HasKey(value => new { value.UserId, value.AttributeDefinitionId });
        builder.Property(value => value.ValueJson).HasMaxLength(4000).IsRequired();
        builder.Property(value => value.CreatedAt).IsRequired();
        builder.HasIndex(value => value.AttributeDefinitionId);

        builder.HasOne(value => value.User)
            .WithMany(user => user.ProfileAttributeValues)
            .HasForeignKey(value => value.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(value => value.AttributeDefinition)
            .WithMany(definition => definition.Values)
            .HasForeignKey(value => value.AttributeDefinitionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
