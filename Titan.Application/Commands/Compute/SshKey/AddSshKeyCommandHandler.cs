using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.SshKey.Contracts;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using SshKeyAggregate = Titan.Domain.Aggregates.SshKey.SshKey;

namespace Titan.Application.Commands.Compute.SshKey;

/// Titan-only: no provider command, so the request and its outcome are recorded in the same save
/// (titan-ops features/activity/design.md#actor-and-tenant).
public class AddSshKeyCommandHandler(
    ISshKeyRepository repo,
    ITenantRepository tenantRepo,
    ICurrentUser currentUser,
    IActivityRecorder activity)
{
    public async Task<AddSshKeyCommandResult> HandleAsync(AddSshKeyCommand command, CancellationToken ct = default)
    {
        Guid tenantId = currentUser.RequireTenantId();
        var tenant = await tenantRepo.FindByIdAsync(tenantId, ct)
            ?? throw new NotFoundException("TENANT_NOT_FOUND", "Tenant not found.");
        int keyCount = await repo.CountByTenantAsync(tenantId, ct);

        SshKeyAggregate key = SshKeyAggregate.Create(tenant, keyCount, command.Name, command.PublicKey);

        bool fingerprintTaken = await repo.ExistsByFingerprintAsync(tenantId, key.Fingerprint, ct);
        if (fingerprintTaken)
        {
            throw new ConflictException("SSH_KEY_EXISTS", $"This key is already saved ({key.Fingerprint}).");
        }

        bool nameTaken = await repo.ExistsByNameAsync(tenantId, key.Name, ct);
        if (nameTaken)
        {
            throw new ConflictException("SSH_KEY_NAME_TAKEN", $"An SSH key named '{key.Name}' already exists.");
        }

        repo.Add(key);
        activity.Stage("ssh_key.create_requested", "ssh_key", key.Id, key.Name);
        activity.StageOutcome(tenantId, "ssh_key.created", "ssh_key", key.Id, key.Name);
        await repo.SaveAsync(ct);

        return new AddSshKeyCommandResult(key.Id);
    }
}
