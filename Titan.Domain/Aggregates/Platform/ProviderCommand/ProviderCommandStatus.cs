namespace Titan.Domain.Aggregates.Platform.ProviderCommand;

/// titan-ops architecture/provider-and-worker.md#provider-command-table — Pending is written in the caller's transaction;
/// a worker's lease moves it to Leased; the worker's report ends it Done or Failed. A Leased row whose lease ran out
/// is claimable again.
public enum ProviderCommandStatus
{
    Pending,
    Leased,
    Done,
    Failed
}
