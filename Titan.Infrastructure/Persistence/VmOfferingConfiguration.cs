using Titan.Domain.Values;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Titan.Domain.Aggregates.VmOffering;
using Titan.Domain.Aggregates.VmTemplate;

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
        builder.Property(o => o.VcpuCount).HasColumnName("vcpu_count").IsRequired();
        builder.Property(o => o.MemoryMb).HasColumnName("memory_mb").IsRequired();
        // NOT NULL: a tier without a price would bill every VM on it zero, silently
        // (OfferingPricingSchemaTests.PriceColumnIsNotNullable). The Rate <-> decimal conversion is a
        // convention in TitanDbContext (titan-ops architecture/money-and-wallet.md#rate-value-object).
        builder.Property(o => o.PricePerHour)
            .HasColumnName("price_per_hour").HasColumnType("decimal(18,10)").IsRequired();

        SeedOfferings(builder);
    }

    // HasData needs constant values: no Guid.NewGuid()/UtcNow.
    // Whole đồng per hour, a decision of the catalog, not a conversion
    // (titan-ops features/catalog/design.md#whole-vnd-prices).
    private static void SeedOfferings(EntityTypeBuilder<VmOffering> builder)
    {
        builder.HasData(
            Row("s", "Small", 1, 2048, 170m),
            Row("m", "Medium", 2, 4096, 320m),
            Row("l", "Large", 4, 8192, 640m),
            Row("xl", "Extra Large", 8, 16384, 1280m));
    }

    // VmOffering has a private ctor + validating factory, so HasData maps by property name.
    private static object Row(
        string id, string name, int vcpuCount, int memoryMb, decimal pricePerHour) => new
    {
        Id = id,
        Name = name,
        VcpuCount = vcpuCount,
        MemoryMb = memoryMb,
        PricePerHour = Rate.Of(pricePerHour),
    };
}
