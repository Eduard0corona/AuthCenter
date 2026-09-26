using AuthCenter.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthCenter.Infrastructure.Persistence.Configurations;

public sealed class ApplicationGovernanceConfiguration : IEntityTypeConfiguration<ApplicationGovernance>
{
    public void Configure(EntityTypeBuilder<ApplicationGovernance> builder)
    {
        builder.ToTable("ApplicationGovernance");
        builder.HasKey(settings => settings.ApplicationSystemId);
        builder.Property(settings => settings.Version).IsConcurrencyToken();
        builder.HasOne(settings => settings.ApplicationSystem).WithOne().HasForeignKey<ApplicationGovernance>(settings => settings.ApplicationSystemId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class ApplicationOwnerConfiguration : IEntityTypeConfiguration<ApplicationOwner>
{
    public void Configure(EntityTypeBuilder<ApplicationOwner> builder)
    {
        builder.ToTable("ApplicationOwners");
        builder.HasKey(owner => owner.Id);
        builder.HasIndex(owner => new { owner.ApplicationSystemId, owner.UserId }).IsUnique();
        builder.HasIndex(owner => owner.UserId);
        builder.HasOne(owner => owner.ApplicationSystem).WithMany().HasForeignKey(owner => owner.ApplicationSystemId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(owner => owner.User).WithMany().HasForeignKey(owner => owner.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class AccessRequestConfiguration : IEntityTypeConfiguration<AccessRequest>
{
    public void Configure(EntityTypeBuilder<AccessRequest> builder)
    {
        builder.ToTable("AccessRequests");
        builder.HasKey(request => request.Id);
        builder.Property(request => request.Justification).HasMaxLength(1000);
        builder.Property(request => request.DecisionComment).HasMaxLength(1000);
        builder.Property(request => request.Version).IsConcurrencyToken();
        builder.HasIndex(request => new { request.ApplicationSystemId, request.Status });
        builder.HasIndex(request => new { request.UserId, request.Status });
        builder.HasIndex(request => new { request.Status, request.ExpiresAt });
        builder.HasOne(request => request.User).WithMany().HasForeignKey(request => request.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(request => request.ApplicationSystem).WithMany().HasForeignKey(request => request.ApplicationSystemId).OnDelete(DeleteBehavior.Cascade);
        // Roles are deactivated, never deleted; NoAction avoids a second cascade path from the application.
        builder.HasOne(request => request.RequestedRole).WithMany().HasForeignKey(request => request.RequestedRoleId).OnDelete(DeleteBehavior.NoAction);
    }
}

public sealed class SeparationOfDutiesRuleConfiguration : IEntityTypeConfiguration<SeparationOfDutiesRule>
{
    public void Configure(EntityTypeBuilder<SeparationOfDutiesRule> builder)
    {
        builder.ToTable("SeparationOfDutiesRules");
        builder.HasKey(rule => rule.Id);
        builder.Property(rule => rule.Name).HasMaxLength(150).IsRequired();
        builder.HasIndex(rule => rule.Name).IsUnique();
        builder.Property(rule => rule.Description).HasMaxLength(1000);
        builder.Property(rule => rule.Version).IsConcurrencyToken();
        builder.HasIndex(rule => rule.FirstRoleId);
        builder.HasIndex(rule => rule.SecondRoleId);
        builder.HasOne(rule => rule.FirstRole).WithMany().HasForeignKey(rule => rule.FirstRoleId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne(rule => rule.SecondRole).WithMany().HasForeignKey(rule => rule.SecondRoleId).OnDelete(DeleteBehavior.NoAction);
    }
}

public sealed class AccessReviewCampaignConfiguration : IEntityTypeConfiguration<AccessReviewCampaign>
{
    public void Configure(EntityTypeBuilder<AccessReviewCampaign> builder)
    {
        builder.ToTable("AccessReviewCampaigns");
        builder.HasKey(campaign => campaign.Id);
        builder.Property(campaign => campaign.Name).HasMaxLength(150).IsRequired();
        builder.Property(campaign => campaign.Version).IsConcurrencyToken();
        builder.HasIndex(campaign => new { campaign.Status, campaign.DueAt });
        builder.HasIndex(campaign => campaign.ApplicationSystemId);
        builder.HasOne(campaign => campaign.ApplicationSystem).WithMany().HasForeignKey(campaign => campaign.ApplicationSystemId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class AccessReviewItemConfiguration : IEntityTypeConfiguration<AccessReviewItem>
{
    public void Configure(EntityTypeBuilder<AccessReviewItem> builder)
    {
        builder.ToTable("AccessReviewItems");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.GroupNames).HasMaxLength(1000);
        builder.Property(item => item.RoleNames).HasMaxLength(1000);
        builder.Property(item => item.Comment).HasMaxLength(1000);
        builder.Property(item => item.Outcome).HasMaxLength(1000);
        builder.Property(item => item.Version).IsConcurrencyToken();
        builder.HasIndex(item => new { item.CampaignId, item.UserId }).IsUnique();
        builder.HasIndex(item => new { item.CampaignId, item.Decision });
        builder.HasIndex(item => item.UserId);
        builder.HasOne(item => item.Campaign).WithMany(campaign => campaign.Items).HasForeignKey(item => item.CampaignId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(item => item.User).WithMany().HasForeignKey(item => item.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
