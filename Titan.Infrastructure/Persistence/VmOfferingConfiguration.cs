using Titan.Domain.Values;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Titan.Domain.Aggregates.Catalog.VmOffering;
using Titan.Domain.Aggregates.Catalog.VmTemplate;

namespace Titan.Infrastructure.Persistence;

public class VmOfferingConfiguration : IEntityTypeConfiguration<VmOffering>
{
    public void Configure(EntityTypeBuilder<VmOffering> builder)
    {
        builder.ToTable("vm_offerings");
        // Slug PK ("s"/"m"/"l"/"xl") — it is also the billing key stored on Vm, a plain string, so
        // a surrogate GUID would only add a lookup (titan-ops features/catalog/design.md#vm-offering-sizing).
        builder.HasKey(o => o.Id);
        builder.Property(o => o.Id).HasColumnName("id").HasColumnType("text");
        builder.Property(o => o.Name).HasColumnName("name").HasColumnType("text").IsRequired();
        builder.Property(o => o.CpuUnits).HasColumnName("cpu_units").HasColumnType("numeric(5,2)").IsRequired();
        builder.Property(o => o.VcpuCount).HasColumnName("vcpu_count").IsRequired();
        builder.Property(o => o.MemoryMb).HasColumnName("memory_mb").IsRequired();
        // NOT NULL: a tier without a price would bill every VM on it zero, silently
        // (OfferingPricingSchemaTests.PriceColumnIsNotNullable). The Rate <-> decimal conversion is a
        // convention in TitanDbContext (titan-ops architecture/money-and-wallet.md#rate-value-object).
        builder.Property(o => o.PricePerHour)
            .HasColumnName("price_per_hour").HasColumnType("decimal(18,10)").IsRequired();

        SeedOfferings(builder);
    }

    // CPU = vCPU × 0.5, the 2:1 overcommit ratio. HasData needs constant values: no Guid.NewGuid()/UtcNow.
    //
    // Positioning: half of Vultr for the equivalent instance.
    // Source: Vultr's public plan API (api.vultr.com/v2/plans?type=vc2), converted at
    // 1 USD = 1.2809 SGD (ECB via Frankfurter, 2026-08-07).
    //
    //   tier  Titan spec      Vultr plan      Vultr $/h   half   SGD/h      × 18000 = VND/h (titan-ops architecture/money-and-wallet.md#money-is-long-vnd)
    //   s     1 vCPU /  2 GB  vc2-1c-2gb        0.014     0.007  0.008966   161.388
    //   m     2 vCPU /  4 GB  vc2-2c-4gb        0.027     0.0135 0.017292   311.256
    //   l     4 vCPU /  8 GB  vc2-4c-8gb        0.055     0.0275 0.035225   634.050
    //   xl    8 vCPU / 16 GB  vc2-6c-16gb *     0.110     0.055  0.070450  1268.100
    //
    // * Vultr has no 8 vCPU / 16 GB plan: their 16 GB plan gives 6 vCPU ($80/mo) and their 8 vCPU
    //   plan gives 32 GB ($160/mo). Anchored on the RAM match, which also keeps the ladder doubling
    //   like the tiers do — so xl deliberately offers more vCPU than Vultr at that price point.
    //
    // Vultr bundles storage in the figures above; Titan charges disk separately, on the volume
    // offering that owns that price (VolumeOfferingConfiguration).
    private static void SeedOfferings(EntityTypeBuilder<VmOffering> builder)
    {
        builder.HasData(
            Row("s", "Small", 0.5m, 1, 2048, 161.388m),
            Row("m", "Medium", 1.0m, 2, 4096, 311.256m),
            Row("l", "Large", 2.0m, 4, 8192, 634.050m),
            Row("xl", "Extra Large", 4.0m, 8, 16384, 1268.100m));
    }

    // VmOffering has a private ctor + validating factory, so HasData maps by property name.
    private static object Row(
        string id, string name, decimal cpuUnits, int vcpuCount, int memoryMb, decimal pricePerHour) => new
    {
        Id = id,
        Name = name,
        CpuUnits = cpuUnits,
        VcpuCount = vcpuCount,
        MemoryMb = memoryMb,
        PricePerHour = Rate.Of(pricePerHour),
    };
}
