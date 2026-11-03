namespace Titan.Domain.Aggregates.ProviderCommand;

/// What enqueuing a key that already has a row does (titan-ops architecture/provider-and-worker.md#provider-command-key).
/// An open row (Pending or Leased) always absorbs the repeat.
public enum ProviderCommandRerun
{
    /// A finished row (Done or Failed) runs again. Every key that uses it carries its request id (VM, volume, volume
    /// snapshot, network, firewall rule, VM snapshot, load balancer, template sync) and is never enqueued twice, so the
    /// rule never applies to it until phase 6 removes it (titan-ops architecture/resource-lifecycle.md#rollout).
    AfterAnyFinish,

    /// Only a Failed row runs again; a Done one absorbs the repeat. The two backups: a repeat of what already happened
    /// must not happen twice.
    AfterFailureOnly
}
