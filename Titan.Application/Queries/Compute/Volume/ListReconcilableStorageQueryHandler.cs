using Titan.Application.Queries.Compute.Volume.Contracts;
using Titan.Domain.Repositories.Compute;
using VmAggregate = Titan.Domain.Aggregates.Compute.Vm.Vm;
using VolumeAggregate = Titan.Domain.Aggregates.Compute.Volume.Volume;

namespace Titan.Application.Queries.Compute.Volume;

// Read side of the storage reconciler (titan-ops architecture/desired-and-current.md#desired-and-current). No
// ICurrentUser: the reconciler acts for no tenant; the endpoint authenticates it by X-Internal-Token.
public class ListReconcilableStorageQueryHandler(
    IVolumeRepository volumes,
    IVolumeSnapshotRepository snapshots,
    IVmRepository vms)
{
    public async Task<ReconcilableStorageQueryResult> HandleAsync(ListReconcilableStorageQuery query, CancellationToken ct = default)
    {
        List<VolumeAggregate> volumeRows = await volumes.ListReconcilableAsync(query.ClusterId, ct);
        var snapshotRows = await snapshots.ListReconcilableAsync(query.ClusterId, ct);

        List<Guid> attachedVmIds = volumeRows
            .Where(v => v.CurrentAttachedVmId != null)
            .Select(v => v.CurrentAttachedVmId!.Value)
            .Distinct()
            .ToList();
        List<VmAggregate> attachedVms = await vms.ListByIdsAsync(attachedVmIds, ct);

        List<ReconcilableVolumeQueryResult> volumeResults = [];
        foreach (VolumeAggregate volume in volumeRows)
        {
            string? attachedExternalVmId = null;
            VmAggregate? attachedVm = attachedVms.FirstOrDefault(vm => vm.Id == volume.CurrentAttachedVmId);
            if (attachedVm != null)
            {
                attachedExternalVmId = attachedVm.ExternalId;
            }
            volumeResults.Add(new ReconcilableVolumeQueryResult(
                volume.Id, volume.RequireExternalId(), volume.CurrentExistence.ToString(), attachedExternalVmId,
                volume.CurrentAttachedElsewhere));
        }

        return new ReconcilableStorageQueryResult(
            volumeResults,
            snapshotRows.Select(ReconcilableVolumeSnapshotQueryResult.From).ToList());
    }
}
