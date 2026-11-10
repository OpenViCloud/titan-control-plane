using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.SshKey.Contracts;
using Titan.Application.Services.Compute;
using Titan.Domain.Repositories;
using SshKeyAggregate = Titan.Domain.Aggregates.SshKey.SshKey;

namespace Titan.Application.Commands.Compute.SshKey;

/// A hard delete: VMs already holding the key keep it (cloud-init wrote it at their first boot). A repeat finds
/// nothing and is 404, like an id that never existed.
public class DeleteSshKeyCommandHandler(ISshKeyRepository repo, ICurrentUser currentUser, IActivityRecorder activity)
{
    public async Task HandleAsync(DeleteSshKeyCommand command, CancellationToken ct = default)
    {
        Guid tenantId = currentUser.RequireTenantId();
        SshKeyAggregate key = await ComputeOwnership.RequireSshKeyAsync(repo, tenantId, command.SshKeyId, ct);

        repo.Remove(key);
        activity.Stage("ssh_key.delete_requested", "ssh_key", key.Id, key.Name);
        activity.StageOutcome(tenantId, "ssh_key.deleted", "ssh_key", key.Id, key.Name);
        await repo.SaveAsync(ct);
    }
}
