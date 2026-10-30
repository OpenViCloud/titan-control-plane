using Titan.Application.Abstractions;
using Titan.Application.Options;
using Titan.Application.Services.Compute;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using VmAggregate = Titan.Domain.Aggregates.Vm.Vm;
using VmTemplateAggregate = Titan.Domain.Aggregates.VmTemplate.VmTemplate;
using Titan.Application.Commands.Compute.Vm.Contracts;

namespace Titan.Application.Commands.Compute.Vm;


public class CreateVmCommandHandler(
    IVmRepository repo,
    INetworkRepository networkRepo,
    ITenantRepository tenantRepo,
    ICurrentUser currentUser,
    IProviderCommandQueue commands,
    IVmOfferingRepository offeringRepo,
    IVmTemplateRepository templateRepo,
    ISshKeyRepository sshKeyRepo,
    IActivityRecorder activity,
    ClusterOptions clusters)
{
    public async Task<CreateVmCommandResult> HandleAsync(CreateVmCommand command, CancellationToken ct = default)
    {
        var req = command;   // the command IS the request shape (titan-ops architecture/code-structure.md#contracts)
        var callerId = currentUser.RequireTenantId();

        var tenant = await tenantRepo.FindByIdAsync(callerId, ct)
            ?? throw new NotFoundException("TENANT_NOT_FOUND", "Tenant not found.");

        // The handler counts; the tenant decides inside Vm.Create, so the check cannot be skipped
        // (titan-ops architecture/tenancy-and-admin.md#tenant-quota).
        var activeVmCount = await repo.CountActiveByTenantAsync(callerId, ct);

        // No money gate here, on purpose: neither DebtCeiling nor an overdue invoice stops creation
        // (titan-ops architecture/money-and-wallet.md#postpaid-no-brake). Quota is the only create-time brake.

        // Constructed before the name/network/offering/template lookups on purpose: the quota refusal
        // lives in the factory, so calling it late would report VM_NAME_TAKEN or NETWORK_NOT_FOUND to a
        // tenant who is out of quota. Nothing is persisted until repo.Add, so an early construction that
        // a later check rejects costs nothing.
        var vm = VmAggregate.Create(
            tenant,
            activeVmCount,
            req.NetworkId,
            req.Name,
            req.VmOfferingId,
            req.TemplateId,
            req.ClusterId,
            req.DiskSizeGb ?? 0,
            req.UserData);

        clusters.ClusterOf(req.ClusterId);

        if (await repo.ExistsByNameAsync(callerId, req.Name, ct))
            throw new ConflictException("VM_NAME_TAKEN", $"VM name '{req.Name}' is already taken.");

        var network = await networkRepo.FindByIdAsync(req.NetworkId, ct);
        if (network is null || network.TenantId != callerId)
            throw new NotFoundException("NETWORK_NOT_FOUND", "Network not found.");

        // The network answers whether it can host a VM and hands over the provider id in one call: no
        // `network.ExternalId!` at the enqueue below.
        var externalNetworkId = network.BeginVmDeployment(req.ClusterId);

        // Resolve the sizing tier here (titan-ops features/catalog/design.md#vm-offering-sizing): the worker has no access to Titan's catalog, so
        // the control plane passes the concrete CPU/vCPU/memory it sets on the instance.
        var offering = await offeringRepo.FindByIdAsync(req.VmOfferingId, ct)
            ?? throw new NotFoundException("VM_OFFERING_NOT_FOUND",
                $"VM offering '{req.VmOfferingId}' not found.");

        // Same translation for the template: customers address it by Titan GUID (titan-ops features/catalog/design.md#template-desired-state), but
        // the provider knows it by its own id. The template hands out the provider template id of the VM's cluster — each cluster has its own
        // (titan-ops features/catalog/design.md#template-per-cluster) — and refuses a cluster it is not synced in.
        // A template customers are not offered does not exist for them (titan-ops features/catalog/design.md#template-listed).
        VmTemplateAggregate? template = null;
        if (Guid.TryParse(req.TemplateId, out var templateId))
        {
            template = await templateRepo.FindByIdAsync(templateId, ct);
        }
        if (template == null || !template.IsListed)
        {
            throw new NotFoundException("TEMPLATE_NOT_FOUND", $"Template '{req.TemplateId}' not found.");
        }
        string externalTemplateId = template.ProviderIdIn(req.ClusterId);

        string? vendorData = await VendorDataForKeysAsync(callerId, req.SshKeyIds, ct);

        repo.Add(vm);
        activity.Stage("vm.create_requested", "vm", vm.Id, vm.Name); // flushed atomically with the VM insert; "vm.created" is the outcome (titan-ops architecture/intent-and-observation.md#intent-has-identity)

        // Enqueued before the save, so the VM row and its provider command commit in one transaction: no VM
        // that the worker never hears of (titan-ops architecture/provider-and-worker.md#provider-command-table).
        await commands.EnqueueVmCreateAsync(
            vm.Id,
            vm.Name,
            vm.VmOfferingId,
            offering.CpuUnits,
            offering.VcpuCount,
            offering.MemoryMb,
            externalTemplateId,
            vm.ClusterId,
            externalNetworkId,
            tenant.ProjectName,
            vm.DiskSizeGb,
            vm.UserData,
            vendorData,
            ct);
        await repo.SaveAsync(ct);

        return new CreateVmCommandResult(vm.Id);
    }

    // Every picked key must be the caller's tenant's: a foreign id is indistinguishable from an unknown one.
    private async Task<string?> VendorDataForKeysAsync(Guid tenantId, List<Guid>? sshKeyIds, CancellationToken ct)
    {
        if (sshKeyIds == null || sshKeyIds.Count == 0)
        {
            return null;
        }

        List<Guid> distinctIds = sshKeyIds.Distinct().ToList();
        var keys = await sshKeyRepo.ListByIdsAsync(tenantId, distinctIds, ct);
        foreach (Guid id in distinctIds)
        {
            bool found = keys.Any(k => k.Id == id);
            if (!found)
            {
                throw new NotFoundException("SSH_KEY_NOT_FOUND", $"SSH key '{id}' not found.");
            }
        }

        List<string> lines = distinctIds.Select(id => keys.First(k => k.Id == id).PublicKey).ToList();
        return VmVendorData.ForKeys(lines);
    }
}
