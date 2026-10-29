using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Titan.Domain.Aggregates.Platform.Tenant;
using Titan.Infrastructure.Persistence;
using TenantAggregate = Titan.Domain.Aggregates.Platform.Tenant.Tenant;

namespace Titan.Tests.Infrastructure;

// titan-ops architecture/tenancy-and-admin.md#tenant-quota — TenantQuota is mapped as a ComplexProperty, so eight
// columns live in core.tenants with no separate entity. The InMemory provider does not enforce any of that, so
// what is pinned here is EF MODEL METADATA — the same technique UsageRecordSchemaTests uses.
public class TenantQuotaSchemaTests
{
    private static readonly (string Column, bool PreExisting)[] Expected =
    [
        ("max_vms", true),
        ("max_networks", true),
        ("max_volumes", false),
        ("max_vm_snapshots_per_vm", false),
        ("max_volume_snapshots_per_volume", false),
        ("max_firewall_rules_per_network", false),
        ("max_public_ipv6", false),
        ("max_ssh_keys", false),
        ("max_load_balancers", false),
    ];

    private static TitanDbContext Context()
    {
        var options = new DbContextOptionsBuilder<TitanDbContext>()
            .UseInMemoryDatabase($"quota-schema-{Guid.NewGuid()}")
            .Options;
        return new TitanDbContext(options);
    }

    [Fact] // every cap maps to core.tenants, and none of them is nullable
    public void AllQuotaColumns_AreMapped_NonNullable_OnTheTenantTable()
    {
        using var ctx = Context();
        var complex = ctx.Model.FindEntityType(typeof(TenantAggregate))!
            .GetComplexProperties()
            .SingleOrDefault(c => c.Name == nameof(TenantAggregate.Quota));

        Assert.NotNull(complex); // if the mapping degrades to a shadow/owned entity, this is the tell

        var mapped = complex!.ComplexType.GetProperties()
            .ToDictionary(p => p.GetColumnName(), p => p.IsNullable);

        Assert.Equal(Expected.Length, mapped.Count);

        foreach (var (column, _) in Expected)
        {
            Assert.True(mapped.ContainsKey(column), $"quota column not mapped: {column}");
            Assert.False(mapped[column], $"quota column is nullable, must not be: {column}");
        }
    }

    [Fact] // the two pre-existing columns keep their names. Renaming them would turn an additive
    // migration into a data migration on a live table.
    public void PreExistingColumns_KeepTheirNames()
    {
        using var ctx = Context();
        var mapped = ctx.Model.FindEntityType(typeof(TenantAggregate))!
            .GetComplexProperties().Single(c => c.Name == nameof(TenantAggregate.Quota))
            .ComplexType.GetProperties().Select(p => p.GetColumnName()).ToHashSet();

        foreach (var (column, preExisting) in Expected.Where(e => e.PreExisting))
        {
            Assert.True(mapped.Contains(column),
                $"pre-existing column {column} is gone — this migration is no longer additive");
            Assert.True(preExisting);
        }
    }
}
