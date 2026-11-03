namespace Titan.Domain.Aggregates.ProviderCommand;

/// What enqueuing a key that already has a row does (titan-ops architecture/provider-and-worker.md#provider-command-key).
/// An open row (Pending or Leased) always absorbs the repeat.
public enum ProviderCommandRerun
{
    /// A finished row (Done or Failed) runs again: a key reused for the same resource, such as a VM snapshot's revert or a
    /// template's sync to one cluster. A key that carries its request id (VM, volume, volume snapshot) is never enqueued
    /// twice, so the rule never applies to it.
    AfterAnyFinish,

    /// Only a Failed row runs again; a Done one absorbs the repeat. load-balancer.update and the two backups: a repeat of
    /// what already happened must not happen twice.
    AfterFailureOnly
}
