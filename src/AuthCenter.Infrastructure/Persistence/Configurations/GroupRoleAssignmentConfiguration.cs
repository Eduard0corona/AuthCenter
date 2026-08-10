using AuthCenter.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthCenter.Infrastructure.Persistence.Configurations;

public class GroupRoleAssignmentConfiguration : IEntityTypeConfiguration<GroupRoleAssignment>
{
    public void Configure(EntityTypeBuilder<GroupRoleAssignment> builder)
    {
        builder.HasKey(assignment => new { assignment.GroupId, assignment.RoleId });
        builder.Property(assignment => assignment.CreatedAt).IsRequired();
        builder.HasIndex(assignment => assignment.RoleId);

        builder.HasOne(assignment => assignment.Group)
            .WithMany(group => group.RoleAssignments)
            .HasForeignKey(assignment => assignment.GroupId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(assignment => assignment.Role)
            .WithMany(role => role.GroupAssignments)
            .HasForeignKey(assignment => assignment.RoleId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
