using FastEndpoints;
using Titan.Application.Queries.Admin.Backup;
using Titan.Application.Queries.Admin.Backup.Contracts;
using Titan.Application.Queries.Platform.Backup;
using Titan.Application.Queries.Platform.Backup.Contracts;

namespace Titan.Infrastructure.Endpoints.Admin;

// The newest backup of each protected resource and its health. Read-only: backing up and restoring happen elsewhere.
// Postgres is the platform's (module Platform); tenants' VMs and volumes are read across tenants (module Admin), and
// Platform cannot read Compute, so the two lists meet here.
public class ListBackupsEndpoint(ListBackupsQueryHandler postgres, ListDiskBackupsQueryHandler disks)
    : EndpointWithoutRequest<BackupsQueryResult>
{
    public override void Configure()
    {
        Get("v1/admin/backups");
        Roles("platform-admin");
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var platform = await postgres.HandleAsync(new ListBackupsQuery(), ct);
        var tenants = await disks.HandleAsync(new ListDiskBackupsQuery(), ct);
        await SendAsync(platform with { Items = platform.Items.Concat(tenants).ToList() }, cancellation: ct);
    }
}
