using Titan.Domain.Values;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Titan.Domain.Aggregates.Billing.UsageRecord;

namespace Titan.Infrastructure.Persistence;

public class UsageRecordConfiguration : IEntityTypeConfiguration<UsageRecord>
{
    public void Configure(EntityTypeBuilder<UsageRecord> builder)
    {
        builder.ToTable("usage_records");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasColumnName("id");
        // The uppercase DB token via the shared mapping, not HasConversion<string>(): the member name "Compute"
        // would break the idempotency key against every existing "COMPUTE" row.
        builder.Property(r => r.ResourceType).HasColumnName("resource_type")
            .HasConversion(v => v.ToDbString(), s => UsageResourceTypeExtensions.FromDbString(s))
            .IsRequired();
        builder.Property(r => r.ResourceId).HasColumnName("resource_id").IsRequired();
        builder.Property(r => r.ResourceName).HasColumnName("resource_name").HasMaxLength(64).IsRequired();
        builder.Property(r => r.TenantId).HasColumnName("tenant_id").IsRequired();
        builder.Property(r => r.Bucket).HasColumnName("bucket").IsRequired();
        builder.Property(r => r.IntervalMinutes).HasColumnName("interval_minutes").IsRequired();
        builder.Property(r => r.Quantity).HasColumnName("quantity").IsRequired();
        builder.Property(r => r.UsageType).HasColumnName("usage_type").IsRequired();
        // decimal(18,10): a flat disk rate like 0.00008773/GB/h needs 8 fractional digits; decimal(10,6)
        // truncates it to 0.000088 (UsageRecordSchemaTests.UnitPricePerHour_HasWidePrecisionToAvoidTruncatingFlatDiskRate).
        // The Rate <-> decimal conversion is a convention in TitanDbContext (titan-ops architecture/money-and-wallet.md#rate-value-object).
        builder.Property(r => r.UnitPricePerHour).HasColumnName("unit_price_per_hour").HasColumnType("decimal(18,10)").IsRequired();
        builder.Property(r => r.Cost).HasColumnName("cost").HasColumnType("decimal(18,10)").IsRequired();
        builder.Property(r => r.Currency).HasColumnName("currency").IsRequired();
        builder.Property(r => r.CollectedAt).HasColumnName("collected_at").IsRequired();

        // One row per resource per bucket, so two ticks cannot double-bill a window. TryInsertAsync's raw
        // "ON CONFLICT (resource_type, resource_id, bucket)" relies on this exact column set
        // (UsageRecordSchemaTests.UniquePerResourceAndBucket).
        builder.HasIndex(r => new { r.ResourceType, r.ResourceId, r.Bucket }).IsUnique();
        builder.HasIndex(r => new { r.TenantId, r.Bucket }); // query by tenant+month
    }
}
