using Microsoft.Extensions.Logging;
using Titan.Application.Commands.Catalog.VmTemplate;
using Titan.Application.Commands.Catalog.VmTemplate.Contracts;
using Titan.Application.Options;
using Titan.Domain.Aggregates.Catalog.VmTemplate;
using Titan.Domain.Repositories.Catalog;
using Titan.Domain.Values;

namespace Titan.Application.Services.Catalog.VmTemplate;

public enum TemplateSyncOutcome { AllSynced, TimedOut, HasFailures, NoZones }


// Drives a full template reconcile to completion: enqueue every template into every zone, then poll the catalog
// until every (template, zone) has its alias. It backs the one-shot `sync-templates` CLI of Titan.Host; no deploy step
// runs it any more. The command only enqueues and the worker reconciles asynchronously, so completion is observed by
// polling. The verdict covers the whole catalog: one zone whose sync gave up makes the run HasFailures even when the other
// zones synced — their sync goes on regardless, the exit code only reports it
// (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/catalog/design.md#template-per-zone).
//
// A job, not a handler: it injects the command handler the way TenantResolutionMiddleware injects Platform's.
public class TemplateSyncRunner(
    SyncTemplatesCommandHandler syncTemplates,
    IVmTemplateRepository repo,
    ZoneOptions zones,
    ILogger<TemplateSyncRunner> logger)
{
    // maxAttempts * pollInterval bounds the wait. `delay` is injected so unit tests run without
    // real sleeps. Short-circuits on the first (template, zone) in Error (it won't self-heal this run).
    public async Task<TemplateSyncRunResult> RunAsync(
        int maxAttempts,
        TimeSpan pollInterval,
        Func<TimeSpan, CancellationToken, Task> delay,
        CancellationToken ct = default)
    {
        // No zone means nothing to sync and nothing left pending — a count that reads as done. The Job gets its zone
        // list from the same config as the API (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/catalog/design.md#template-per-zone); without it, fail.
        bool noZoneConfigured = zones.Zones.Count == 0;
        if (noZoneConfigured)
        {
            logger.LogError("Template sync: no zone configured (Clusters__*); nothing can be synced");
            return Result(TemplateSyncOutcome.NoZones, 0, 0, 0, 0);
        }

        await syncTemplates.HandleAsync(new SyncTemplatesCommand(), ct);

        for (var attempt = 1; ; attempt++)
        {
            var all = await repo.ListAllAsync(ct);
            int total = 0, synced = 0, failed = 0;
            foreach (var t in all)
            {
                foreach (ZoneConfig zone in zones.Zones)
                {
                    total++;
                    // A zone with no row yet has not been asked: it counts as pending, like a sync still waiting.
                    VmTemplateZone? row = t.ZoneOrNull(zone.Id);
                    if (row == null)
                    {
                        continue;
                    }
                    bool aliasIsThere = row.CurrentExistence == CurrentExistence.Exists;
                    bool syncGaveUp = row.RequestFailureReason != null && row.PendingIntentId == null;
                    if (aliasIsThere)
                    {
                        synced++;
                    }
                    else if (syncGaveUp)
                    {
                        failed++;
                    }
                }
            }
            var pending = total - synced - failed;

            if (failed > 0)
                return Result(TemplateSyncOutcome.HasFailures, total, synced, failed, pending);
            if (pending == 0)
                return Result(TemplateSyncOutcome.AllSynced, total, synced, failed, pending);
            if (attempt >= maxAttempts)
                return Result(TemplateSyncOutcome.TimedOut, total, synced, failed, pending);

            await delay(pollInterval, ct);
        }
    }

    private TemplateSyncRunResult Result(TemplateSyncOutcome outcome, int total, int synced, int failed, int pending)
    {
        logger.LogInformation("Template sync result: {Outcome} (total={Total} synced={Synced} failed={Failed} pending={Pending})",
            outcome, total, synced, failed, pending);
        return new TemplateSyncRunResult(outcome, total, synced, failed, pending);
    }
}
