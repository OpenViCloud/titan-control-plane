using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Titan.Domain.Aggregates.Vm;
using Titan.Infrastructure.Persistence;

namespace Titan.Tests.Infrastructure;

// The VM metric table (titan-ops features/vm/design.md#vm-metrics): one row per VM per sample time, gone with its VM.
// The InMemory provider enforces neither key nor cascade, so this pins the model metadata the migration builds them from.
public class VmMetricSampleSchemaTests
{
    private static IEntityType Samples()
    {
        var options = new DbContextOptionsBuilder<TitanDbContext>()
            .UseInMemoryDatabase($"schema-{Guid.NewGuid()}")
            .Options;
        using var db = new TitanDbContext(options);
        return db.Model.FindEntityType(typeof(VmMetricSample))!;
    }

    [Fact]
    public void MappedToTheVmMetricSamplesTable()
    {
        Assert.Equal("vm_metric_samples", Samples().GetTableName());
        Assert.Equal("core", Samples().GetSchema());
    }

    // The key is what makes a batch sent twice one row.
    [Fact]
    public void TheKey_IsTheVmAndTheSampleTime()
    {
        IKey key = Samples().FindPrimaryKey()!;

        Assert.Equal(["vm_id", "sampled_at"], key.Properties.Select(p => p.GetColumnName()));
    }

    // Cascade: the samples are the VM's history, nothing else points at them.
    [Fact]
    public void VmId_IsAForeignKeyToVms_WithCascade()
    {
        IForeignKey foreignKey = Samples().GetForeignKeys().Single();

        Assert.Equal(typeof(Vm), foreignKey.PrincipalEntityType.ClrType);
        Assert.Equal([nameof(VmMetricSample.VmId)], foreignKey.Properties.Select(p => p.Name));
        Assert.Equal(DeleteBehavior.Cascade, foreignKey.DeleteBehavior);
    }

    // Incus may report no memory or root filesystem number (the root filesystem only comes with the guest agent): those
    // four may be NULL.
    [Fact]
    public void TheColumns_AndWhichMayBeNull()
    {
        Dictionary<string, bool> nullable = Samples().GetProperties().ToDictionary(p => p.GetColumnName(), p => p.IsNullable);

        Assert.Equal(new Dictionary<string, bool>
        {
            ["vm_id"] = false,
            ["sampled_at"] = false,
            ["source"] = false,
            ["cpu_seconds"] = false,
            ["memory_total_bytes"] = true,
            ["memory_available_bytes"] = true,
            ["root_fs_size_bytes"] = true,
            ["root_fs_avail_bytes"] = true,
            ["disk_read_bytes"] = false,
            ["disk_written_bytes"] = false,
        }, nullable);
    }
}
