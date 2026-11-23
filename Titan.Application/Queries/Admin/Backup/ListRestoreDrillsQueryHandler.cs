using System.Text.Json;
using Titan.Application.Options;
using Titan.Application.Queries.Admin.Backup.Contracts;
using Titan.Domain.Aggregates.ProviderCommand;
using Titan.Domain.Repositories;
using ProviderCommandAggregate = Titan.Domain.Aggregates.ProviderCommand.ProviderCommand;

namespace Titan.Application.Queries.Admin.Backup;

/// The latest restore drill of every configured cluster, from the provider command rows (no table of its own: the
/// row is the result). The newest drill that ended decides: Done is verified, Failed is not, and the line keeps the last
/// good time (titan-ops features/backup/design.md#restore-drill). The retention job deletes finished rows after 30 days,
/// so a cluster whose last drill is older shows none.
public class ListRestoreDrillsQueryHandler(IProviderCommandRepository rows, TimeProvider clock, ClusterOptions clusters)
{
    private static readonly TimeSpan Window = TimeSpan.FromDays(30);

    public async Task<IReadOnlyList<RestoreDrillQueryResult>> HandleAsync(ListRestoreDrillsQuery query, CancellationToken ct = default)
    {
        DateTimeOffset since = clock.GetUtcNow() - Window;
        var results = new List<RestoreDrillQueryResult>();
        foreach (ClusterConfig cluster in clusters.Clusters)
        {
            var ended = (await rows.ListOfKindSinceAsync(ProviderCommandKinds.RestoreDrill, cluster.Id, since, ct))
                .Where(r => r.Status == ProviderCommandStatus.Done || r.Status == ProviderCommandStatus.Failed)
                .OrderByDescending(r => r.CreatedAt)
                .ToList();
            results.Add(Line(cluster, ended));
        }
        return results;
    }

    private static RestoreDrillQueryResult Line(ClusterConfig cluster, List<ProviderCommandAggregate> ended)
    {
        if (ended.Count == 0)
        {
            return new RestoreDrillQueryResult(cluster.Id, cluster.Name, "none", null, null, null, null);
        }
        ProviderCommandAggregate newest = ended[0];
        (string? kind, string? name) = Describe(newest);
        if (newest.Status == ProviderCommandStatus.Done)
        {
            return new RestoreDrillQueryResult(cluster.Id, cluster.Name, "verified", newest.CreatedAt, kind, name, null);
        }
        DateTimeOffset? lastGood = ended.FirstOrDefault(r => r.Status == ProviderCommandStatus.Done)?.CreatedAt;
        return new RestoreDrillQueryResult(cluster.Id, cluster.Name, "notVerified", lastGood, kind, name, newest.LastError);
    }

    // A row whose payload this reader does not know still counts as a drill; it only has no name to show.
    private static (string? Kind, string? Name) Describe(ProviderCommandAggregate row)
    {
        try
        {
            using JsonDocument payload = JsonDocument.Parse(row.Payload);
            return (payload.RootElement.GetProperty("kind").GetString(), payload.RootElement.GetProperty("name").GetString());
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return (null, null);
        }
    }
}
