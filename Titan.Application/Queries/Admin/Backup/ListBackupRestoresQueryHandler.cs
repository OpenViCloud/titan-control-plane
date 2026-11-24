using System.Text.Json;
using Titan.Application.Options;
using Titan.Application.Queries.Admin.Backup.Contracts;
using Titan.Application.Services.Platform.Backup;
using Titan.Domain.Aggregates.ProviderCommand;
using Titan.Domain.Repositories;
using ProviderCommandAggregate = Titan.Domain.Aggregates.ProviderCommand.ProviderCommand;

namespace Titan.Application.Queries.Admin.Backup;

/// The admin's restores of a backup archive as a new VM or volume, newest first, from the provider command rows (no table
/// of its own: the row is the restore, titan-ops features/backup/design.md#restore-as-new-resource). The retention job
/// deletes finished rows after 30 days, so the list keeps to the last 30. A row of no tenant: the owner is read from the
/// payload, and the name from the new resource's row.
public class ListBackupRestoresQueryHandler(
    IProviderCommandRepository rows,
    IVmRepository vms,
    IVolumeRepository volumes,
    TimeProvider clock,
    ClusterOptions clusters)
{
    private static readonly TimeSpan Window = TimeSpan.FromDays(30);

    public async Task<IReadOnlyList<BackupRestoreQueryResult>> HandleAsync(ListBackupRestoresQuery query, CancellationToken ct = default)
    {
        DateTimeOffset since = clock.GetUtcNow() - Window;
        // A restore runs on its source's cluster, which the configuration names, as the drills' list reads them.
        var restores = new List<(string Kind, ProviderCommandAggregate Row, Guid ResourceId)>();
        foreach (ClusterConfig cluster in clusters.Clusters)
        {
            foreach ((string kind, string commandKind) in new[] { ("vm", ProviderCommandKinds.VmRestore), ("volume", ProviderCommandKinds.VolumeRestore) })
            {
                foreach (ProviderCommandAggregate row in await rows.ListOfKindSinceAsync(commandKind, cluster.Id, since, ct))
                {
                    // The queue writes every restore with the new resource on its row; one without names nothing to show.
                    if (row.ResourceId is Guid resourceId)
                    {
                        restores.Add((kind, row, resourceId));
                    }
                }
            }
        }

        Dictionary<Guid, string> vmNames = (await vms.ListByIdsAsync(Ids(restores, "vm"), ct)).ToDictionary(v => v.Id, v => v.Name);
        Dictionary<Guid, string> volumeNames = (await volumes.ListByIdsAsync(Ids(restores, "volume"), ct)).ToDictionary(v => v.Id, v => v.Name);

        var lines = new List<BackupRestoreQueryResult>();
        foreach ((string kind, ProviderCommandAggregate row, Guid resourceId) in restores.OrderByDescending(r => r.Row.CreatedAt))
        {
            Dictionary<Guid, string> names = kind == "vm" ? vmNames : volumeNames;
            (Guid? tenantId, DateTimeOffset? takenAt) = Describe(row);
            (string state, string? reason) = StateOf(row);
            lines.Add(new BackupRestoreQueryResult(kind, resourceId, names.GetValueOrDefault(resourceId), tenantId, takenAt,
                state, row.CreatedAt, reason));
        }
        return lines;
    }

    private static List<Guid> Ids(List<(string Kind, ProviderCommandAggregate Row, Guid ResourceId)> restores, string kind) =>
        restores.Where(r => r.Kind == kind).Select(r => r.ResourceId).Distinct().ToList();

    // Pending again after a retryable failure is still running: its last error is not the restore's answer yet.
    private static (string State, string? Reason) StateOf(ProviderCommandAggregate row) => row.Status switch
    {
        ProviderCommandStatus.Done => ("done", null),
        ProviderCommandStatus.Failed => ("failed", row.LastError),
        _ => ("running", null),
    };

    // A row whose payload this reader does not know still counts as a restore; it only has no owner or archive time to show.
    private static (Guid? TenantId, DateTimeOffset? TakenAt) Describe(ProviderCommandAggregate row)
    {
        try
        {
            using JsonDocument payload = JsonDocument.Parse(row.Payload);
            Guid? tenantId = null;
            if (payload.RootElement.TryGetProperty("tenantId", out JsonElement tenant) && tenant.ValueKind == JsonValueKind.String
                && tenant.TryGetGuid(out Guid parsed))
            {
                tenantId = parsed;
            }
            DateTimeOffset? takenAt = null;
            if (payload.RootElement.TryGetProperty("backupKey", out JsonElement key) && key.ValueKind == JsonValueKind.String)
            {
                takenAt = IncusBackupKey.TakenAtOf(key.GetString()!);
            }
            return (tenantId, takenAt);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return (null, null);
        }
    }
}
