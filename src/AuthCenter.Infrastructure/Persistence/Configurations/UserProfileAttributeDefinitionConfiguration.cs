using AuthCenter.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthCenter.Infrastructure.Persistence.Configurations;

public sealed class UserProfileAttributeDefinitionConfiguration : IEntityTypeConfiguration<UserProfileAttributeDefinition>
{
    public void Configure(EntityTypeBuilder<UserProfileAttributeDefinition> builder)
    {
        builder.HasKey(definition => definition.Id);
        builder.Property(definition => definition.Key).HasMaxLength(100).IsRequired();
        builder.Property(definition => definition.DisplayName).HasMaxLength(200).IsRequired();
        builder.Property(definition => definition.Description).HasMaxLength(1000);
        builder.Property(definition => definition.DataType).HasConversion<string>().HasMaxLength(20);
        builder.Property(definition => definition.DefaultValueJson).HasMaxLength(4000);
        builder.Property(definition => definition.MinimumNumber).HasPrecision(38, 10);
        builder.Property(definition => definition.MaximumNumber).HasPrecision(38, 10);
        builder.Property(definition => definition.ValidationPattern).HasMaxLength(500);
        builder.Property(definition => definition.AllowedValuesJson).HasMaxLength(4000);
        builder.Property(definition => definition.CreatedAt).IsRequired();
        builder.HasIndex(definition => definition.Key).IsUnique();
        builder.HasIndex(definition => definition.IsActive);
    }
}
