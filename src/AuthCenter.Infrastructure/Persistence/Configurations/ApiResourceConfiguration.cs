using AuthCenter.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthCenter.Infrastructure.Persistence.Configurations;

public class ApiResourceConfiguration : IEntityTypeConfiguration<ApiResource>
{
    public void Configure(EntityTypeBuilder<ApiResource> builder)
    {
        builder.HasKey(resource => resource.Id);
        builder.Property(resource => resource.Identifier).HasMaxLength(300).IsRequired();
        builder.Property(resource => resource.DisplayName).HasMaxLength(200).IsRequired();
        builder.Property(resource => resource.Description).HasMaxLength(1000);
        builder.HasIndex(resource => resource.Identifier).IsUnique();
        builder.HasIndex(resource => resource.ApplicationSystemId);
        builder.HasOne(resource => resource.ApplicationSystem)
            .WithMany()
            .HasForeignKey(resource => resource.ApplicationSystemId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public class ApiScopeConfiguration : IEntityTypeConfiguration<ApiScope>
{
    public void Configure(EntityTypeBuilder<ApiScope> builder)
    {
        builder.HasKey(scope => scope.Id);
        builder.Property(scope => scope.Name).HasMaxLength(128).IsRequired();
        builder.Property(scope => scope.DisplayName).HasMaxLength(200).IsRequired();
        builder.Property(scope => scope.Description).HasMaxLength(1000);
        builder.HasIndex(scope => scope.Name).IsUnique();
        builder.HasOne(scope => scope.ApiResource)
            .WithMany(resource => resource.Scopes)
            .HasForeignKey(scope => scope.ApiResourceId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
