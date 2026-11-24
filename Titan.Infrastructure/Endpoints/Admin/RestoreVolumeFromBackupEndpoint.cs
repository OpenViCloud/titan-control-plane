using Microsoft.AspNetCore.Http;
using FastEndpoints;
using Titan.Application.Commands.Admin.Backup;
using Titan.Application.Commands.Admin.Backup.Contracts;
using Titan.Infrastructure.Auth;

namespace Titan.Infrastructure.Endpoints.Admin;

// The volume whose archive is restored rides the route, so the BODY record carries the archive and the new volume's
// name (titan-ops architecture/code-structure.md#command-is-wire-contract).
public record RestoreVolumeFromBackupBody(string BackupKey, string Name);

// An admin write on a tenant's resources (titan-ops features/backup/design.md#restore-as-new-resource): it makes a new
// volume of that tenant, so it answers like a create, with the new id and nothing else, and a repeat is replayed by its
// Idempotency-Key (titan-ops architecture/http-contract.md#idempotency-key).
public class RestoreVolumeFromBackupEndpoint(RestoreVolumeFromBackupCommandHandler handler)
    : Endpoint<RestoreVolumeFromBackupBody, RestoreVolumeFromBackupCommandResult>, IRequiresIdempotencyKey
{
    public override void Configure()
    {
        Post("v1/admin/backups/volumes/{volumeId:guid}/restore");
        Roles("platform-admin");
        Description(b => b.ClearDefaultProduces(200).Produces<RestoreVolumeFromBackupCommandResult>(202, "application/json"));
    }

    public override async Task HandleAsync(RestoreVolumeFromBackupBody body, CancellationToken ct)
    {
        var result = await handler.HandleAsync(
            new RestoreVolumeFromBackupCommand(Route<Guid>("volumeId"), body.BackupKey, body.Name), ct);
        await SendAsync(result, 202, ct);
    }
}
