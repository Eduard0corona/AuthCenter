using AuthCenter.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthCenter.Infrastructure.Persistence.Configurations;

public class ApplicationAccessPolicyRuleConfiguration : IEntityTypeConfiguration<ApplicationAccessPolicyRule>
{
    public void Configure(EntityTypeBuilder<ApplicationAccessPolicyRule> builder)
    {
        builder.HasKey(rule => rule.Id);
        builder.Property(rule => rule.Name).HasMaxLength(200).IsRequired();
        builder.Property(rule => rule.IncludedIpCidrsJson).HasMaxLength(4000);
        builder.Property(rule => rule.ExcludedIpCidrsJson).HasMaxLength(4000);
        builder.Property(rule => rule.Action).HasConversion<string>().HasMaxLength(20);
        builder.Property(rule => rule.MfaRequirement).HasConversion<string>().HasMaxLength(20);
        builder.Property(rule => rule.CreatedAt).IsRequired();
        builder.HasIndex(rule => new { rule.ApplicationSystemId, rule.Priority }).IsUnique();
        builder.HasIndex(rule => new { rule.ApplicationSystemId, rule.IsActive });

        builder.HasOne(rule => rule.ApplicationSystem)
            .WithMany(application => application.AccessPolicyRules)
            .HasForeignKey(rule => rule.ApplicationSystemId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(rule => rule.DirectoryGroup)
            .WithMany(group => group.AccessPolicyRules)
            .HasForeignKey(rule => rule.DirectoryGroupId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
