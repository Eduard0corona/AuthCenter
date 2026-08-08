using AuthCenter.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthCenter.Infrastructure.Persistence.Configurations;

public sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.HasKey(message => message.Id);
        builder.Property(message => message.Type).HasMaxLength(100).IsRequired();
        builder.Property(message => message.ProtectedPayload).IsRequired();
        builder.Property(message => message.LastError).HasMaxLength(2000);
        builder.Property(message => message.LockedUntil).IsConcurrencyToken();
        builder.HasIndex(message => new { message.ProcessedAt, message.NextAttemptAt, message.LockedUntil });
    }
}
