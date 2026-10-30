using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Titan.Domain.Aggregates.Vm;
using Titan.Infrastructure.Persistence;

namespace Titan.Tests.Infrastructure;

// The InMemory provider enforces no index, so the model metadata the migration is generated from is what gets
// pinned here. The filter must say what VmRepository.ExistsByNameAsync says: a name is free again only once the VM
// is Destroyed. A stricter index answers the handler's "free" with a unique violation, a 500.
public class VmSchemaTests
{
    private static IIndex NameIndex()
    {
        var options = new DbContextOptionsBuilder<TitanDbContext>()
            .UseInMemoryDatabase($"schema-{Guid.NewGuid()}")
            .Options;
        using var db = new TitanDbContext(options);
        return db.Model.FindEntityType(typeof(Vm))!.GetIndexes().Single(i =>
            i.Properties.Select(p => p.Name).SequenceEqual([nameof(Vm.TenantId), nameof(Vm.Name)]));
    }

    [Fact]
    public void VmName_IsUniquePerTenant_OnlyAmongVmsNotDeleted()
    {
        IIndex index = NameIndex();

        Assert.True(index.IsUnique);
        Assert.Equal("current_existence <> 'Deleted'", index.GetFilter());
    }
}
