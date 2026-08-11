using AuthCenter.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthCenter.Infrastructure.Persistence.Configurations;

public class UserGroupMembershipConfiguration : IEntityTypeConfiguration<UserGroupMembership>
{
    public void Configure(EntityTypeBuilder<UserGroupMembership> builder)
    {
        builder.HasKey(membership => new { membership.GroupId, membership.UserId });
        builder.Property(membership => membership.CreatedAt).IsRequired();
        builder.HasIndex(membership => membership.UserId);

        builder.HasOne(membership => membership.Group)
            .WithMany(group => group.Memberships)
            .HasForeignKey(membership => membership.GroupId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(membership => membership.User)
            .WithMany(user => user.GroupMemberships)
            .HasForeignKey(membership => membership.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
