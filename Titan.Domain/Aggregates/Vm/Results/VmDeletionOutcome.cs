namespace Titan.Domain.Aggregates.Vm;

/// What Vm.MarkDeleted did, in one answer: the intent the report fulfilled (Delete, or null), and which of the VM's
/// snapshots had a delete of their own waiting that the VM's delete finished for them. One call tells the callback
/// both, so it cannot record the VM's outcome and forget the snapshots'.
public record VmDeletionOutcome(VmIntent? Fulfilled, IReadOnlyList<VmSnapshot> SnapshotsWhoseDeleteWasFulfilled);
