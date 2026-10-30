using Titan.Domain.Values;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Titan.Domain.Aggregates.VolumeOffering;

namespace Titan.Infrastructure.Persistence;

public class VolumeOfferingConfiguration : IEntityTypeConfiguration<VolumeOffering>
{
    public void Configure(EntityTypeBuilder<VolumeOffering> builder)
    {
        builder.ToTable("volume_offerings");
        builder.HasKey(o => o.Id);
        builder.Property(o => o.Id).HasColumnName("id").HasColumnType("text");
        builder.Property(o => o.Name).HasColumnName("name").HasColumnType("text").IsRequired();
        builder.Property(o => o.SizeGb).HasColumnName("size_gb").IsRequired();
        builder.Property(o => o.IsCustomized).HasColumnName("is_customized").IsRequired();
        // The Rate <-> decimal conversion is a convention in TitanDbContext (titan-ops architecture/money-and-wallet.md#rate-value-object).
        builder.Property(o => o.PricePerGbPerHour)
            .HasColumnName("price_per_gb_per_hour").HasColumnType("decimal(18,10)").IsRequired();

        // A single tier: every volume is a custom volume in the storage pool of its cluster, and invented
        // tiers would advertise choices that do not exist.
        //
        // "Half of Vultr NVMe block storage": $0.10/GB/mo -> half $0.05 -> x1.2809 USD/SGD -> S$0.00008773/GB/h,
        // x18000 -> 1.57914 VND/GB/h.
        builder.HasData(new
        {
            Id = "standard",
            Name = "Standard",
            SizeGb = 0,
            IsCustomized = true,
            PricePerGbPerHour = Rate.Of(1.57914m),
        });
    }
}
