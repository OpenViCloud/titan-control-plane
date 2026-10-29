using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Titan.Domain.Aggregates.Catalog.VmOffering;
using Titan.Domain.Aggregates.Catalog.VolumeOffering;
using Titan.Domain.Aggregates.Compute.Vm;
using Titan.Domain.Aggregates.Compute.Volume;
using Titan.Infrastructure.Persistence;

namespace Titan.Tests.Infrastructure;

// titan-ops features/catalog/design.md#resources-reference-their-offering — a VM or volume always has a price on file: the
// database refuses a row that names an offering the catalog does not have, and refuses to delete an
// offering something still points at. InMemory enforces no foreign key, so this pins the model metadata
// the migration is generated from.
public class OfferingForeignKeySchemaTests
{
    private static IForeignKey ForeignKey(Type dependent, string property)
    {
        var options = new DbContextOptionsBuilder<TitanDbContext>()
            .UseInMemoryDatabase($"schema-{Guid.NewGuid()}")
            .Options;
        using var db = new TitanDbContext(options);
        var foreignKey = db.Model.FindEntityType(dependent)!
            .GetForeignKeys()
            .SingleOrDefault(f => f.Properties.Count == 1 && f.Properties[0].Name == property);
        Assert.NotNull(foreignKey);
        return foreignKey;
    }

    [Fact]
    public void Volume_PointsAtAVolumeOffering_AndBlocksItsDelete()
    {
        var foreignKey = ForeignKey(typeof(Volume), nameof(Volume.VolumeOfferingId));

        Assert.Equal(typeof(VolumeOffering), foreignKey.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.Restrict, foreignKey.DeleteBehavior);
    }

    [Fact]
    public void Vm_PointsAtAVmOffering_AndBlocksItsDelete()
    {
        var foreignKey = ForeignKey(typeof(Vm), nameof(Vm.VmOfferingId));

        Assert.Equal(typeof(VmOffering), foreignKey.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.Restrict, foreignKey.DeleteBehavior);
    }
}
