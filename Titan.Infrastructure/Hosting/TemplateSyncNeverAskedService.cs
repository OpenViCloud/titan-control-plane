using Microsoft.Extensions.Logging;
using Titan.Application.Commands.Catalog.VmTemplate;
using Titan.Application.Commands.Catalog.VmTemplate.Contracts;
using Titan.Application.Queries.Catalog.VmTemplate;
using Titan.Application.Queries.Catalog.VmTemplate.Contracts;

namespace Titan.Infrastructure.Hosting;

/// <summary>
/// Runs the admin's sync-all (SyncTemplatesCommandHandler) when a template was never asked into a configured cluster, so a new
/// environment or a new cluster gets its catalog without an admin; not a deploy hook, so an unreachable cluster blocks no
/// deploy (titan-ops features/catalog/design.md#template-sync-never-asked).
/// </summary>
public class TemplateSyncNeverAskedService(
    ListClustersWithNeverAskedTemplatesQueryHandler neverAsked,
    SyncTemplatesCommandHandler syncAll,
    ILogger<TemplateSyncNeverAskedService> logger) : IScheduledJob
{
    public const string JobName = "template-sync-never-asked";

    public string Name => JobName;

    public Task RunAsync(CancellationToken ct) => RunOnceAsync(neverAsked, syncAll, logger, ct);

    // One tick: nothing never asked ⇒ nothing sent, so a failed sync waits for the admin instead of repeating.
    public static async Task RunOnceAsync(
        ListClustersWithNeverAskedTemplatesQueryHandler neverAsked,
        SyncTemplatesCommandHandler syncAll,
        ILogger logger,
        CancellationToken ct)
    {
        List<string> clusterIds = await neverAsked.HandleAsync(new ListClustersWithNeverAskedTemplatesQuery(), ct);
        if (clusterIds.Count == 0)
        {
            return;
        }
        logger.LogInformation("Templates never asked into cluster(s) {ClusterIds}: syncing every template",
            string.Join(", ", clusterIds));
        await syncAll.HandleAsync(new SyncTemplatesCommand(), ct);
    }
}
