using Titan.Domain.Values;

namespace Titan.Domain.Aggregates.Vm;

/// What Vm.EndOperation ended, in one answer: the operation the report named, and — when a delete ended — the snapshots
/// whose own delete was waiting and went with the VM (titan-ops architecture/resource-lifecycle.md#shape: the one place an
/// ending returns more than one operation). The handler records each outcome once from this.
public sealed record VmOperationEnded(PendingOperation<VmOperationKind> Operation, IReadOnlyList<VmSnapshot> SnapshotsWhoseDeleteEnded);
