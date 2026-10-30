using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Titan.Domain.Aggregates.Volume;
using Titan.Infrastructure.Persistence;

namespace Titan.Tests.Infrastructure;

// A volume snapshot is the volume's child entity (titan-ops architecture/code-structure.md#child-entity): its row belongs
// to a volume row, and the table keeps its name and columns. The InMemory provider does not enforce keys the way
// Postgres does, so this pins the model metadata the migration builds the constraint from.
public class VolumeSnapshotSchemaTests
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
    public void StillMappedToTheVolumeSnapshotsTable()
    {
        Assert.Equal("volume_snapshots", Model().FindEntityType(typeof(VolumeSnapshot))!.GetTableName());
    }

    // Restrict, not cascade: no row of either table is ever deleted, and a snapshot whose volume row is missing is a
    // broken record the database must refuse, not silently drop.
    [Fact]
    public void VolumeId_IsAForeignKeyToVolumes_WithoutCascade()
    {
        var foreignKey = Model().FindEntityType(typeof(VolumeSnapshot))!.GetForeignKeys().Single();

        Assert.Equal(typeof(Volume), foreignKey.PrincipalEntityType.ClrType);
        Assert.Equal([nameof(VolumeSnapshot.VolumeId)], foreignKey.Properties.Select(p => p.Name));
        Assert.Equal(DeleteBehavior.Restrict, foreignKey.DeleteBehavior);
    }

    [Fact] // the volume loads its snapshots through the backing field: no public setter on the collection
    public void Volume_OwnsTheSnapshotsNavigation()
    {
        var navigation = Model().FindEntityType(typeof(Volume))!.FindNavigation(nameof(Volume.Snapshots))!;

        Assert.Equal(typeof(VolumeSnapshot), navigation.TargetEntityType.ClrType);
        Assert.Equal(PropertyAccessMode.Field, navigation.GetPropertyAccessMode());
        Assert.True(navigation.IsCollection);
    }
}
