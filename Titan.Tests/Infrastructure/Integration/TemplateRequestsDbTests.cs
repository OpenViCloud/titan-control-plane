using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Catalog.VmTemplate;
using Titan.Application.Commands.Catalog.VmTemplate.Contracts;
using Titan.Application.Options;
using Titan.Domain.Aggregates.ProviderCommand;
using Titan.Domain.Aggregates.VmTemplate;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.ProviderCommands;
using Titan.Infrastructure.Repositories;

namespace Titan.Tests.Infrastructure.Integration;

/// What only Postgres can show of titan-ops architecture/resource-lifecycle.md for templates: the row lock that makes two
/// sync-alls (the admin's and the five-minute tick) take turns (§one-key-per-request), and the stuck-request query
/// joining a sync to its command row with no tenant (§lost-and-stale). Its own database: a sync-all asks every template
/// there is. Category=Integration — its own CI step.
[Trait("Category", "Integration")]
public sealed class TemplateRequestsDbTests : IAsyncLifetime
{
    private static readonly ClusterOptions Clusters = new()
    {
        Clusters = [new ClusterConfig { Id = "cluster-a", Name = "A" }, new ClusterConfig { Id = "cluster-b", Name = "B" }],
    };

    private readonly TestPostgres _container = new();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await using TitanDbContext db = NewDbContext();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    private TitanDbContext NewDbContext() =>
        new(new DbContextOptionsBuilder<TitanDbContext>().UseNpgsql(_container.GetConnectionString()).Options);

    private async Task SyncAllThroughTheHandlerAsync()
    {
        await using TitanDbContext db = NewDbContext();
        var handler = new SyncTemplatesCommandHandler(new VmTemplateRepository(db), Clusters,
            new ProviderCommandQueue(new ProviderCommandRepository(db), TimeProvider.System),
            NullLogger<SyncTemplatesCommandHandler>.Instance);
        await handler.HandleAsync(new SyncTemplatesCommand());
    }

    private async Task<List<VmTemplate>> TemplatesAsync()
    {
        await using TitanDbContext db = NewDbContext();
        return await new VmTemplateRepository(db).ListAllAsync();
    }

    private async Task<List<string>> TemplateSyncKeysAsync()
    {
        await using TitanDbContext db = NewDbContext();
        return await db.Set<ProviderCommand>().Where(c => c.Key.StartsWith("template-sync-")).Select(c => c.Key).ToListAsync();
    }

    // The second waits on each template's row lock, then sees the first's sync waiting and asks nothing: one request and
    // one command row per (template, cluster), and neither run fails.
    [Fact]
    public async Task TwoSyncAlls_AtOnce_LeaveOneRequest_AndOneCommandRow_PerTemplatePerCluster()
    {
        await Task.WhenAll(SyncAllThroughTheHandlerAsync(), SyncAllThroughTheHandlerAsync());

        List<VmTemplate> templates = await TemplatesAsync();
        Assert.NotEmpty(templates);
        var expected = new List<string>();
        foreach (VmTemplate template in templates)
        {
            Assert.Equal(["cluster-a", "cluster-b"], template.Clusters.Select(c => c.ClusterId).OrderBy(id => id, StringComparer.Ordinal));
            foreach (VmTemplateCluster row in template.Clusters)
            {
                Assert.Equal(VmTemplateRequestKind.Sync, row.Pending!.Kind);
                expected.Add($"template-sync-{template.Id}-{row.ClusterId}-{row.Pending.Id}");
            }
        }
        Assert.Equal(expected.OrderBy(k => k, StringComparer.Ordinal), (await TemplateSyncKeysAsync()).OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public async Task AStuckSync_IsListedWithNoTenant_AndItsCommandRowsStatus()
    {
        await SyncAllThroughTheHandlerAsync();
        Guid templateId = (await TemplatesAsync())[0].Id;
        await using (TitanDbContext db = NewDbContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE core.vm_template_clusters SET pending_request_asked_at = now() - make_interval(mins => 20) WHERE template_id = {templateId} AND cluster_id = 'cluster-a'");
        }

        await using TitanDbContext read = NewDbContext();
        IReadOnlyList<StuckRequest> stuck = await new PendingRequestReader(read).ListStuckAsync(TimeSpan.FromMinutes(15));

        StuckRequest sync = Assert.Single(stuck);
        Assert.Equal(("template", templateId, (Guid?)null, "Sync", "Pending", true),
            (sync.Resource, sync.Id, sync.TenantId, sync.Kind, sync.CommandStatus, sync.CommandIsOpen));
    }
}
