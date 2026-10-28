using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Titan.Application.Commands.Catalog.VmTemplate;
using Titan.Application.Commands.Catalog.VmTemplate.Contracts;
using Titan.Application.Queries.Catalog.VmTemplate;
using Titan.Application.Queries.Catalog.VmTemplate.Contracts;

namespace Titan.Infrastructure.Hosting;

/// <summary>
/// Runs the admin's sync-all (SyncTemplatesCommandHandler) when a template was never asked into a configured cluster, so a new
/// environment or a new cluster gets its catalog without an admin; not a deploy hook, so an unreachable cluster blocks no
/// deploy (https://github.com/OpenViCloud/titan-ops/blob/main/docs/features/catalog/design.md#template-sync-never-asked).
/// </summary>
public class TemplateSyncNeverAskedService(
    IServiceScopeFactory scopeFactory,
    ILogger<TemplateSyncNeverAskedService> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        // Wait-first, like the other hosted services: nothing runs during startup or in a WebApplicationFactory test.
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                await RunOnceAsync(
                    scope.ServiceProvider.GetRequiredService<ListClustersWithNeverAskedTemplatesQueryHandler>(),
                    scope.ServiceProvider.GetRequiredService<SyncTemplatesCommandHandler>(),
                    logger, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Template sync of never-asked clusters failed");
            }
        }
    }

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
