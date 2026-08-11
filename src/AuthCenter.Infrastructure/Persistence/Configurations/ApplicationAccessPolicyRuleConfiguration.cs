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
        builder.Property(rule => rule.ActiveDaysUtcJson).HasMaxLength(200);
        builder.Property(rule => rule.Action).HasConversion<string>().HasMaxLength(20);
        builder.Property(rule => rule.MfaRequirement).HasConversion<string>().HasMaxLength(20);
        builder.Property(rule => rule.MinimumRiskLevel).HasConversion<string>().HasMaxLength(20);
        builder.Property(rule => rule.MaximumRiskLevel).HasConversion<string>().HasMaxLength(20);
        builder.Property(rule => rule.RequiredAssuranceLevel).HasConversion<string>().HasMaxLength(20);
        builder.Property(rule => rule.CreatedAt).IsRequired();
        builder.HasIndex(rule => new { rule.PolicyVersionId, rule.Priority }).IsUnique();
        builder.HasIndex(rule => new { rule.PolicyVersionId, rule.IsActive });

        builder.HasOne(rule => rule.PolicyVersion)
            .WithMany(version => version.Rules)
            .HasForeignKey(rule => new { rule.PolicyVersionId, rule.ApplicationSystemId })
            .HasPrincipalKey(version => new { version.Id, version.ApplicationSystemId })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(rule => rule.ApplicationSystem)
            .WithMany(application => application.AccessPolicyRules)
            .HasForeignKey(rule => rule.ApplicationSystemId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(rule => rule.DirectoryGroup)
            .WithMany(group => group.AccessPolicyRules)
            .HasForeignKey(rule => rule.DirectoryGroupId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(rule => rule.User)
            .WithMany(user => user.AccessPolicyRules)
            .HasForeignKey(rule => rule.UserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
