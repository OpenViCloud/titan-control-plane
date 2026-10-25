using System.Text.Json.Serialization;

namespace Titan.Application.Queries.Platform.Backup.Contracts;

// Reason names why a health is not Ok (see BackupHealthPolicy); TenantId is for the backups of a tenant's VMs and volumes.
public record BackupItemQueryResult(
    string Kind, string Name, Guid? TenantId, DateTimeOffset? TakenAt, long? SizeBytes,
    [property: JsonConverter(typeof(JsonStringEnumConverter))] BackupHealth Health, string? Reason, DateTimeOffset? LastWalAt);
