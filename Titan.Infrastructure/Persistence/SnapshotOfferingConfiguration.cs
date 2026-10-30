using Titan.Domain.Values;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Titan.Domain.Aggregates.SnapshotOffering;

namespace Titan.Infrastructure.Persistence;

public class SnapshotOfferingConfiguration : IEntityTypeConfiguration<SnapshotOffering>
{
    public void Configure(EntityTypeBuilder<SnapshotOffering> builder)
    {
        builder.ToTable("snapshot_offerings");
        builder.HasKey(o => o.Id);
        builder.Property(o => o.Id).HasColumnName("id").HasColumnType("text");
        builder.Property(o => o.Name).HasColumnName("name").HasColumnType("text").IsRequired();
        // The Rate <-> decimal conversion is a convention in TitanDbContext (titan-ops architecture/money-and-wallet.md#rate-value-object).
        builder.Property(o => o.PricePerHour)
            .HasColumnName("price_per_hour").HasColumnType("decimal(18,10)").IsRequired();

        // One row at the id the meter resolves: snapshots carry no offering id, so this is not a tier a customer
        // picks (titan-ops architecture/money-and-wallet.md#price-belongs-to-offering).
        // 1.8 VND/snapshot/h ≈ 1314 VND/mo, a placeholder rate. Per snapshot, not per GB: Titan tracks no snapshot
        // size. 0 switches snapshot billing off; a missing row fails the meter tick.
        builder.HasData(new
        {
            Id = SnapshotOffering.DefaultId,
            Name = "Standard",
            PricePerHour = Rate.Of(1.8m),
        });
    }
}
