using Microsoft.AspNetCore.Http;
using FastEndpoints;
using Titan.Application.Commands.Admin.Backup;
using Titan.Application.Commands.Admin.Backup.Contracts;
using Titan.Infrastructure.Auth;

namespace Titan.Infrastructure.Endpoints.Admin;

// The VM whose archive is restored rides the route, so the BODY record carries the archive and the new VM's name
// (titan-ops architecture/code-structure.md#command-is-wire-contract).
public record RestoreVmFromBackupBody(string BackupKey, string Name);

// An admin write on a tenant's resources (titan-ops features/backup/design.md#restore-as-new-resource): it makes a new
// VM of that tenant, so it answers like a create, with the new id and nothing else, and a repeat is replayed by its
// Idempotency-Key (titan-ops architecture/http-contract.md#idempotency-key).
public class RestoreVmFromBackupEndpoint(RestoreVmFromBackupCommandHandler handler)
    : Endpoint<RestoreVmFromBackupBody, RestoreVmFromBackupCommandResult>, IRequiresIdempotencyKey
{
    public override void Configure()
    {
        Post("v1/admin/backups/vms/{vmId:guid}/restore");
        Roles("platform-admin");
        Description(b => b.ClearDefaultProduces(200).Produces<RestoreVmFromBackupCommandResult>(202, "application/json"));
    }

    public override async Task HandleAsync(RestoreVmFromBackupBody body, CancellationToken ct)
    {
        var result = await handler.HandleAsync(
            new RestoreVmFromBackupCommand(Route<Guid>("vmId"), body.BackupKey, body.Name), ct);
        await SendAsync(result, 202, ct);
    }
}
