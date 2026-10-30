using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Titan.Domain.Aggregates.Vm;
using Titan.Infrastructure.Persistence;

namespace Titan.Tests.Infrastructure;

// A VM snapshot is the VM's child entity (titan-ops architecture/code-structure.md#child-entity): its row belongs to a VM
// row, and the table keeps its name and columns. The InMemory provider does not enforce keys the way Postgres does, so
// this pins the model metadata the migration builds the constraint from.
public class VmSnapshotSchemaTests
{
    private static IModel Model()
    {
        var options = new DbContextOptionsBuilder<TitanDbContext>()
            .UseInMemoryDatabase($"schema-{Guid.NewGuid()}")
            .Options;
        using var db = new TitanDbContext(options);
        return db.Model;
    }

    [Fact]
    public void StillMappedToTheVmSnapshotsTable()
    {
        Assert.Equal("vm_snapshots", Model().FindEntityType(typeof(VmSnapshot))!.GetTableName());
    }

    // Restrict, not cascade: no row of either table is ever deleted, and a snapshot whose VM row is missing is a
    // broken record the database must refuse, not silently drop.
    [Fact]
    public void VmId_IsAForeignKeyToVms_WithoutCascade()
    {
        var foreignKey = Model().FindEntityType(typeof(VmSnapshot))!.GetForeignKeys().Single();

        Assert.Equal(typeof(Vm), foreignKey.PrincipalEntityType.ClrType);
        Assert.Equal([nameof(VmSnapshot.VmId)], foreignKey.Properties.Select(p => p.Name));
        Assert.Equal(DeleteBehavior.Restrict, foreignKey.DeleteBehavior);
    }

    [Fact] // the VM loads its snapshots through the backing field: no public setter on the collection
    public void Vm_OwnsTheSnapshotsNavigation()
    {
        var navigation = Model().FindEntityType(typeof(Vm))!.FindNavigation(nameof(Vm.Snapshots))!;

        Assert.Equal(typeof(VmSnapshot), navigation.TargetEntityType.ClrType);
        Assert.Equal(PropertyAccessMode.Field, navigation.GetPropertyAccessMode());
        Assert.True(navigation.IsCollection);
    }
}
