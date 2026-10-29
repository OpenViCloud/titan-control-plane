using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Titan.Domain.Aggregates.Catalog.VmTemplate;
using Titan.Infrastructure.Persistence;

namespace Titan.Tests.Infrastructure;

// "One sync state per template per cluster" (titan-ops features/catalog/design.md#template-per-cluster) is the primary key of
// vm_template_clusters, not application code. The InMemory provider does not enforce keys the way Postgres does, so this
// pins the model metadata the migration builds the table from.
public class VmTemplateClusterSchemaTests
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
    public void MappedToVmTemplateClustersTable()
    {
        Assert.Equal("vm_template_clusters", Model().FindEntityType(typeof(VmTemplateCluster))!.GetTableName());
    }

    [Fact] // a single-column key would let one template hold two states for a cluster, or one state for two templates
    public void PrimaryKey_IsTheTemplateAndTheCluster()
    {
        var key = Model().FindEntityType(typeof(VmTemplateCluster))!.FindPrimaryKey()!;

        Assert.Equal(
            [nameof(VmTemplateCluster.TemplateId), nameof(VmTemplateCluster.ClusterId)],
            key.Properties.Select(p => p.Name));
    }

    [Fact] // the cluster rows belong to their template: deleting a template takes its sync states with it
    public void BelongsToItsTemplate_DeletedWithIt()
    {
        var foreignKey = Model().FindEntityType(typeof(VmTemplateCluster))!.GetForeignKeys().Single();

        Assert.Equal(typeof(VmTemplate), foreignKey.PrincipalEntityType.ClrType);
        Assert.Equal([nameof(VmTemplateCluster.TemplateId)], foreignKey.Properties.Select(p => p.Name));
        Assert.Equal(DeleteBehavior.Cascade, foreignKey.DeleteBehavior);
    }

    [Theory] // the state moved into the cluster rows: a column left on vm_templates would be a second, stale answer
    [InlineData("current_state")]
    [InlineData("desired_state")]
    [InlineData("pending_intent_id")]
    [InlineData("current_state_reason")]
    [InlineData("external_template_id")]
    public void VmTemplates_NoLongerHoldsAnySyncState(string column)
    {
        var template = Model().FindEntityType(typeof(VmTemplate))!;

        Assert.DoesNotContain(column, template.GetProperties().Select(p => p.GetColumnName()));
    }
}
