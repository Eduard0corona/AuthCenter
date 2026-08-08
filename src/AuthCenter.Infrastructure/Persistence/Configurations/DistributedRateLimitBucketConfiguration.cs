using AuthCenter.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuthCenter.Infrastructure.Persistence.Configurations;

public sealed class DistributedRateLimitBucketConfiguration : IEntityTypeConfiguration<DistributedRateLimitBucket>
{
    public void Configure(EntityTypeBuilder<DistributedRateLimitBucket> builder)
    {
        builder.HasKey(bucket => bucket.Key);
        builder.Property(bucket => bucket.Key).HasMaxLength(128);
        builder.HasIndex(bucket => bucket.ExpiresAt);
    }
}
