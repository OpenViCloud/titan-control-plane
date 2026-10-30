using Titan.Domain.Model;
using Titan.Domain.Values;

namespace Titan.Domain.Aggregates.VmOffering;

// Sizing tier, decoupled from the OS template (titan-ops features/catalog/design.md#vm-offering-sizing). Never synced
// to the provider: the sizing travels with each VM create.
//
// Id is a readable slug ("s"/"m"/"l"/"xl") because Vm.VmOfferingId is a plain string used as the billing key.
//
// The price is a NOT NULL column on the tier (titan-ops architecture/money-and-wallet.md#price-belongs-to-offering):
// a tier with no price would bill VMs zero, silently.
public class VmOffering : AggregateRoot
{
    public string Id { get; private set; } = default!;
    public string Name { get; private set; } = default!;
    public decimal CpuUnits { get; private set; }
    public int VcpuCount { get; private set; }
    public int MemoryMb { get; private set; }

    /// VND per hour (titan-ops architecture/money-and-wallet.md#rate-value-object). The factory takes decimal because
    /// prices arrive from seed data and admin JSON as primitives.
    public Rate PricePerHour { get; private set; } = default!;

    private VmOffering() { }

    public static VmOffering Create(
        string id, string name, decimal cpuUnits, int vcpuCount, int memoryMb, decimal pricePerHour)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("id must not be empty", nameof(id));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name must not be empty", nameof(name));
        // Guard here rather than at create: a non-positive size is rejected by the provider far
        // downstream, where the error no longer points back at the catalog row that caused it.
        if (cpuUnits <= 0) throw new ArgumentException("cpuUnits must be > 0", nameof(cpuUnits));
        if (vcpuCount <= 0) throw new ArgumentException("vcpuCount must be > 0", nameof(vcpuCount));
        if (memoryMb <= 0) throw new ArgumentException("memoryMb must be > 0", nameof(memoryMb));
        ValidatePrice(pricePerHour);

        return new VmOffering
        {
            Id = id,
            Name = name,
            CpuUnits = cpuUnits,
            VcpuCount = vcpuCount,
            MemoryMb = memoryMb,
            PricePerHour = Rate.Of(pricePerHour)
        };
    }

    /// Does not rewrite anything already billed: usage records hold the unit price they were collected at
    /// (titan-ops architecture/money-and-wallet.md#invoice-frozen-snapshot). The caller records the change as an
    /// AdminAuditLog row; the tier holds only the price in force.
    public void ChangePrice(decimal pricePerHour)
    {
        ValidatePrice(pricePerHour); // stricter than Rate.Of: a VM tier may not be free
        PricePerHour = Rate.Of(pricePerHour);
    }

    // A tier priced at zero is not free — it is a catalog row that bills nothing while consuming real
    // capacity. Guarded on the aggregate so no path (API, seed, migration) can produce one.
    private static void ValidatePrice(decimal pricePerHour)
    {
        if (pricePerHour <= 0)
            throw new ArgumentException("pricePerHour must be > 0", nameof(pricePerHour));
    }
}
