using Microsoft.Extensions.Logging;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Catalog.VmTemplate.Contracts;
using Titan.Application.Options;
using Titan.Domain.Aggregates.Catalog.VmTemplate;
using Titan.Domain.Repositories.Catalog;
using Titan.Domain.Values;
using VmTemplateAggregate = Titan.Domain.Aggregates.Catalog.VmTemplate.VmTemplate;

namespace Titan.Application.Commands.Catalog.VmTemplate;

// Returns nothing (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/http-contract.md#async-actions-return-202): the enqueue is async, the worker
// reconciles later, and the admin reads the outcome back from the template list. The per-run counts are
// logged below.
public class SyncTemplatesCommandHandler(
    IVmTemplateRepository repo,
    ClusterOptions clusters,
    IProviderCommandQueue commands,
    ILogger<SyncTemplatesCommandHandler> logger)
{
    // Add-only reconcile (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/catalog/design.md#template-sync-add-only) into EVERY configured cluster
    // (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/catalog/design.md#template-per-cluster): the worker creates the templates missing on that cluster's
    // cluster and no-ops the ones whose image alias it finds, never updating or deleting an existing one. One enqueue
    // failure is counted and logged, not fatal — the other templates and clusters still enqueue, and a re-trigger is safe.
    // The sync requests and the provider commands are saved once, at the end, so they commit together
    // (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/provider-and-worker.md#provider-command-table).
    public async Task HandleAsync(SyncTemplatesCommand command, CancellationToken ct = default)
    {
        var templates = await repo.ListAllAsync(ct);

        // A cluster the template is not yet synced in gets a sync request before it is enqueued, so the admin list
        // shows it waiting. A cluster that has the alias asks for nothing, and one with a sync waiting is the repeat
        // (https://github.com/OpenViCloud/titan-ops/blob/main/docs/architecture/intent-and-observation.md#domain-refuses-a-repeat): only its command is enqueued again.
        // Loaded untracked (ListAllAsync), so the requests are saved through the tracked copy.
        foreach (var t in templates)
        {
            List<string> clustersToAsk = clusters.Clusters.Select(z => z.Id).Where(clusterId => NeedsASyncRequest(t, clusterId)).ToList();
            if (clustersToAsk.Count == 0)
            {
                continue;
            }
            var tracked = await repo.FindByIdAsync(t.Id, ct);
            if (tracked == null)
            {
                continue;
            }
            foreach (string clusterId in clustersToAsk)
            {
                tracked.RequestSyncIn(clusterId);
            }
        }

        int enqueued = 0, failed = 0;
        foreach (var t in templates)
        {
            foreach (ClusterConfig cluster in clusters.Clusters)
            {
                try
                {
                    await commands.EnqueueTemplateSyncAsync(t.Id, cluster.Id, t.Name, t.Description, t.OsImageName,
                        t.ImageUrl, t.RootDiskSizeGb, ct);
                    enqueued++;
                }
                catch (Exception ex)
                {
                    failed++;
                    logger.LogError(ex, "Template sync enqueue failed for {TemplateId} ({Name}) in cluster {ClusterId}",
                        t.Id, t.Name, cluster.Id);
                }
            }
        }
        await repo.SaveAsync(ct);

        logger.LogInformation("Template sync-all: {Enqueued} enqueued, {Failed} failed of {Total} (templates × clusters)",
            enqueued, failed, templates.Count * clusters.Clusters.Count);
    }

    private static bool NeedsASyncRequest(VmTemplateAggregate template, string clusterId)
    {
        VmTemplateCluster? row = template.ClusterOrNull(clusterId);
        if (row == null)
        {
            return true;
        }
        bool aliasIsThere = row.CurrentExistence == CurrentExistence.Exists;
        bool syncIsWaiting = row.PendingIntentId != null;
        return !aliasIsThere && !syncIsWaiting;
    }
}
