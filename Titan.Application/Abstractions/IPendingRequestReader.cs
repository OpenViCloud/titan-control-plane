namespace Titan.Application.Abstractions;

/// Reads the waiting requests through the view core.pending_requests (the tables that reached this rollout phase),
/// joined to the command row each request's key names (titan-ops architecture/resource-lifecycle.md#lost-and-stale). A
/// request waiting longer than the threshold is stuck: with its command row still open the worker is slow or absent; with
/// the row Done, Failed or missing no command will report it. A VM stop waits the threshold plus the stop timeout its
/// command carries, `Vm.DefaultStopTimeoutSeconds` when it carries none or has no row
/// (titan-ops features/vm/design.md#vm-stop-timeout).
public interface IPendingRequestReader
{
    Task<IReadOnlyList<StuckRequest>> ListStuckAsync(TimeSpan olderThan, CancellationToken ct = default);
}

/// One stuck request. TenantId is null for the sync of a template or a function runtime: a catalog belongs to no tenant. CommandStatus is the
/// command row's status word, null when no row carries the request's key.
public record StuckRequest(
    string Resource,
    Guid Id,
    Guid? TenantId,
    Guid RequestId,
    string Kind,
    DateTimeOffset AskedAt,
    string? CommandStatus)
{
    /// Pending or Leased: the worker has not finished it. Anything else: no command will report this request.
    public bool CommandIsOpen => CommandStatus is "Pending" or "Leased";
}
