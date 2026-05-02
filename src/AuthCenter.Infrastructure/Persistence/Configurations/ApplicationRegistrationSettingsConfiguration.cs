using AuthCenter.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthCenter.Infrastructure.Persistence.Configurations;

public class ApplicationRegistrationSettingsConfiguration : IEntityTypeConfiguration<ApplicationRegistrationSettings>
{
    public void Configure(EntityTypeBuilder<ApplicationRegistrationSettings> builder)
    {
        builder.HasKey(s => s.Id);
        builder.Property(s => s.AllowedEmailDomains).HasMaxLength(1000);
        builder.Property(s => s.CreatedAt).IsRequired();

        builder.HasIndex(s => s.ApplicationSystemId).IsUnique();
    }
}
