using Titan.Domain.Values;

namespace Titan.Domain.Aggregates.Vm;

/// What Vm.EndRequest ended, in one answer: the request the report named, and — when a delete ended — the snapshots
/// whose own delete was waiting and went with the VM (titan-ops architecture/resource-lifecycle.md#shape: the one place an
/// ending returns more than one request). The handler records each outcome once from this.
public sealed record VmRequestEnded(PendingRequest<VmRequestKind> Request, IReadOnlyList<VmSnapshot> SnapshotsWhoseDeleteEnded);
