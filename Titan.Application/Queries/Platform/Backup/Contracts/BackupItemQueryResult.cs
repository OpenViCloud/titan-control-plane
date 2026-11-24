using System.Text.Json.Serialization;

namespace Titan.Application.Queries.Platform.Backup.Contracts;

// Reason names why a health is not Ok (see BackupHealthPolicy); TenantId is for the backups of a tenant's VMs and volumes.
// ResourceId and Archives are what an admin restores a VM or volume from: its Titan id and every Complete archive of it,
// newest first (titan-ops features/backup/design.md#restore-as-new-resource). The platform's database and a cluster's
// config are never restored as a new resource: no id, an empty list.
public record BackupItemQueryResult(
    string Kind, string Name, Guid? TenantId, DateTimeOffset? TakenAt, long? SizeBytes,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] BackupHealth Health, string? Reason, DateTimeOffset? LastWalAt,
    Guid? ResourceId, IReadOnlyList<BackupArchiveQueryResult> Archives);
