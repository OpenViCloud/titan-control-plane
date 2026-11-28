using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Titan.Domain.Aggregates.FunctionRuntime;
using Titan.Infrastructure.Persistence;

namespace Titan.Tests.Infrastructure;

// "One sync state per runtime per function cluster" and "one runtime per name" are keys of the tables, not application
// code (titan-ops features/ovc-function/design.md#runtimes). The InMemory provider does not enforce them, so this pins the
// model metadata the migration builds the tables from, as VmTemplateClusterSchemaTests does for templates.
public class FunctionRuntimeSchemaTests
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
    public void MappedToTheirTables_InCore()
    {
        IEntityType runtime = Model().FindEntityType(typeof(FunctionRuntime))!;
        IEntityType cluster = Model().FindEntityType(typeof(FunctionRuntimeCluster))!;

        Assert.Equal(("function_runtimes", "core"), (runtime.GetTableName(), runtime.GetSchema()));
        Assert.Equal(("function_runtime_clusters", "core"), (cluster.GetTableName(), cluster.GetSchema()));
    }

    [Fact] // global: the catalog has no tenant
    public void TheCatalog_HasNoTenant()
    {
        IEntityType runtime = Model().FindEntityType(typeof(FunctionRuntime))!;

        Assert.DoesNotContain("tenant_id", runtime.GetProperties().Select(p => p.GetColumnName()));
    }

    [Fact]
    public void TheName_IsUnique()
    {
        IIndex index = Model().FindEntityType(typeof(FunctionRuntime))!.GetIndexes()
            .Single(i => i.Properties.Select(p => p.Name).SequenceEqual([nameof(FunctionRuntime.Name)]));

        Assert.True(index.IsUnique);
    }

    [Fact] // a single-column key would let one runtime hold two states for a cluster
    public void ClusterPrimaryKey_IsTheRuntimeAndTheCluster()
    {
        IKey key = Model().FindEntityType(typeof(FunctionRuntimeCluster))!.FindPrimaryKey()!;

        Assert.Equal([nameof(FunctionRuntimeCluster.RuntimeId), nameof(FunctionRuntimeCluster.ClusterId)], key.Properties.Select(p => p.Name));
    }

    [Fact]
    public void ClusterRows_BelongToTheirRuntime_DeletedWithIt()
    {
        IForeignKey foreignKey = Model().FindEntityType(typeof(FunctionRuntimeCluster))!.GetForeignKeys().Single();

        Assert.Equal(typeof(FunctionRuntime), foreignKey.PrincipalEntityType.ClrType);
        Assert.Equal(DeleteBehavior.Cascade, foreignKey.DeleteBehavior);
    }

    // The columns of a template's cluster row (vm_template_clusters), so core.pending_requests reads both the same way.
    [Theory]
    [InlineData("runtime_id")]
    [InlineData("cluster_id")]
    [InlineData("status")]
    [InlineData("pending_request_id")]
    [InlineData("pending_request_asked_at")]
    [InlineData("request_failure_reason")]
    [InlineData("request_failure_detail")]
    [InlineData("request_failed_at")]
    [InlineData("observed_state")]
    public void ClusterRows_KeepTheTemplatesColumns(string column)
    {
        IEntityType cluster = Model().FindEntityType(typeof(FunctionRuntimeCluster))!;
        IEnumerable<string?> columns = cluster.GetProperties().Select(p => p.GetColumnName())
            .Concat(cluster.GetNavigations().Where(n => n.TargetEntityType.IsOwned())
                .SelectMany(n => n.TargetEntityType.GetProperties().Select(p => p.GetColumnName())));

        Assert.Contains(column, columns);
    }

    [Fact] // a status stored as a number would read as another status the day the enum is reordered
    public void TheStatus_IsStoredAsItsName()
    {
        IProperty status = Model().FindEntityType(typeof(FunctionRuntimeCluster))!.FindProperty(nameof(FunctionRuntimeCluster.Status))!;

        Assert.Equal(typeof(string), status.GetProviderClrType());
    }
}
