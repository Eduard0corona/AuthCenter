using AuthCenter.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthCenter.Infrastructure.Persistence.Configurations;

public class GroupApplicationAssignmentConfiguration : IEntityTypeConfiguration<GroupApplicationAssignment>
{
    public void Configure(EntityTypeBuilder<GroupApplicationAssignment> builder)
    {
        builder.HasKey(assignment => new { assignment.GroupId, assignment.ApplicationSystemId });
        builder.Property(assignment => assignment.CreatedAt).IsRequired();
        builder.HasIndex(assignment => assignment.ApplicationSystemId);

        builder.HasOne(assignment => assignment.Group)
            .WithMany(group => group.ApplicationAssignments)
            .HasForeignKey(assignment => assignment.GroupId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(assignment => assignment.ApplicationSystem)
            .WithMany(application => application.GroupAssignments)
            .HasForeignKey(assignment => assignment.ApplicationSystemId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
