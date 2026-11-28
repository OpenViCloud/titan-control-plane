using Microsoft.Extensions.Logging;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Catalog.FunctionRuntime.Contracts;
using Titan.Application.Options;
using Titan.Domain.Repositories;
using FunctionRuntimeAggregate = Titan.Domain.Aggregates.FunctionRuntime.FunctionRuntime;

namespace Titan.Application.Commands.Catalog.FunctionRuntime;

// Returns nothing (titan-ops architecture/http-contract.md#async-actions-return-202): the worker pulls later, and the admin
// reads the outcome back from the runtime list.
public class SyncFunctionRuntimesCommandHandler(
    IFunctionRuntimeRepository repo,
    FunctionClusterOptions clusters,
    IProviderCommandQueue commands,
    ILogger<SyncFunctionRuntimesCommandHandler> logger)
{
    // As SyncTemplatesCommandHandler, into every configured function cluster (titan-ops features/ovc-function/design.md#runtimes):
    // a cluster with no row, a Failed or a NotFound one gets a sync and one command keyed on it; one that holds the image or
    // whose sync waits gets nothing (titan-ops architecture/resource-lifecycle.md#repeat). One save at the end, so the syncs
    // and their commands commit together; a failed enqueue saves nothing.
    public async Task HandleAsync(SyncFunctionRuntimesCommand command, CancellationToken ct = default)
    {
        // Untracked, to choose which runtimes to lock; each is locked in id order, so two sync-alls take turns on each
        // runtime in the same order and never deadlock, then read again under the lock
        // (titan-ops architecture/resource-lifecycle.md#one-key-per-request).
        List<FunctionRuntimeAggregate> runtimes = await repo.ListAllAsync(ct);
        List<Guid> toAsk = runtimes
            .Where(r => clusters.Clusters.Any(cluster => r.NeedsSyncIn(cluster.Id)))
            .Select(r => r.Id)
            .OrderBy(id => id)
            .ToList();

        int enqueued = 0;
        foreach (Guid runtimeId in toAsk)
        {
            FunctionRuntimeAggregate? locked = await repo.FindByIdForUpdateAsync(runtimeId, ct);
            if (locked == null)
            {
                continue;
            }
            foreach (FunctionClusterConfig cluster in clusters.Clusters)
            {
                if (!locked.NeedsSyncIn(cluster.Id))
                {
                    continue;
                }
                Guid requestId = locked.SyncIn(cluster.Id);
                await commands.EnqueueFunctionRuntimeSyncAsync(locked, cluster.Id, requestId, ct);
                enqueued++;
            }
        }
        await repo.SaveAsync(ct);

        int total = runtimes.Count * clusters.Clusters.Count;
        logger.LogInformation(
            "Function runtime sync-all: {Enqueued} enqueued, {Untouched} there or waiting, of {Total} (runtimes × function clusters)",
            enqueued, total - enqueued, total);
    }
}
