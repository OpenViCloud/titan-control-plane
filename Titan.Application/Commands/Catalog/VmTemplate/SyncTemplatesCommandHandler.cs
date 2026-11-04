using Microsoft.Extensions.Logging;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Catalog.VmTemplate.Contracts;
using Titan.Application.Options;
using Titan.Domain.Aggregates.VmTemplate;
using Titan.Domain.Repositories;
using Titan.Domain.Values;
using VmTemplateAggregate = Titan.Domain.Aggregates.VmTemplate.VmTemplate;

namespace Titan.Application.Commands.Catalog.VmTemplate;

// Returns nothing (titan-ops architecture/http-contract.md#async-actions-return-202): the enqueue is async, the worker
// reconciles later, and the admin reads the outcome back from the template list. The per-run counts are
// logged below.
public class SyncTemplatesCommandHandler(
    IVmTemplateRepository repo,
    ClusterOptions clusters,
    IProviderCommandQueue commands,
    ILogger<SyncTemplatesCommandHandler> logger)
{
    // Add-only (titan-ops features/catalog/design.md#template-sync-add-only) into EVERY configured cluster
    // (titan-ops features/catalog/design.md#template-per-cluster): a cluster with no row, or one without the alias and with
    // nothing waiting, gets a sync request and one command keyed on it. A cluster that has the alias, or whose sync
    // waits, gets nothing: the repeat sends nothing (titan-ops architecture/resource-lifecycle.md#repeat). The requests and
    // their commands are saved once, at the end, so they commit together
    // (titan-ops architecture/provider-and-worker.md#provider-command-table). A failed enqueue fails the whole run and
    // saves nothing: a request kept without its command would wait for good, since a waiting cluster is sent nothing.
    public async Task HandleAsync(SyncTemplatesCommand command, CancellationToken ct = default)
    {
        // Untracked, to choose which templates to lock. Each chosen one is locked in id order, so two sync-alls (the
        // admin's and the five-minute tick) take turns on each template in the same order and never deadlock, then read
        // again under the lock: the second sees what the first left waiting (titan-ops architecture/resource-lifecycle.md#one-key-per-request).
        List<VmTemplateAggregate> templates = await repo.ListAllAsync(ct);
        List<Guid> toAsk = templates
            .Where(t => clusters.Clusters.Any(cluster => NeedsASyncRequest(t, cluster.Id)))
            .Select(t => t.Id)
            .OrderBy(id => id)
            .ToList();

        int enqueued = 0;
        foreach (Guid templateId in toAsk)
        {
            VmTemplateAggregate? locked = await repo.FindByIdForUpdateAsync(templateId, ct);
            if (locked == null)
            {
                continue;
            }
            foreach (ClusterConfig cluster in clusters.Clusters)
            {
                if (!NeedsASyncRequest(locked, cluster.Id))
                {
                    continue;
                }
                PendingOperation<VmTemplateOperationKind> request = locked.SyncIn(cluster.Id);
                await commands.EnqueueTemplateSyncAsync(locked.Id, cluster.Id, request.Id, locked.Name, locked.Description,
                    locked.OsImageName, locked.ImageUrl, locked.RootDiskSizeGb, ct);
                enqueued++;
            }
        }
        await repo.SaveAsync(ct);

        int total = templates.Count * clusters.Clusters.Count;
        logger.LogInformation(
            "Template sync-all: {Enqueued} enqueued, {Untouched} there or waiting, of {Total} (templates × clusters)",
            enqueued, total - enqueued, total);
    }

    private static bool NeedsASyncRequest(VmTemplateAggregate template, string clusterId)
    {
        VmTemplateCluster? row = template.Clusters.FirstOrDefault(candidate => candidate.ClusterId == clusterId);
        if (row == null)
        {
            return true;
        }
        bool aliasIsThere = row.CurrentExistence == CurrentExistence.Exists;
        bool syncIsWaiting = row.Pending != null;
        return !aliasIsThere && !syncIsWaiting;
    }
}
