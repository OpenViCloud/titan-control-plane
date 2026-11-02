namespace Titan.Application.Abstractions;

/// Reads the waiting requests through the view core.pending_requests (the tables that reached this rollout phase),
/// joined to the command row each request's key names (titan-ops architecture/resource-lifecycle.md#lost-and-stale). A
/// request waiting longer than the threshold is stuck: with its command row still open the worker is slow or absent; with
/// the row Done, Failed or missing no command will report it.
public interface IPendingRequestReader
{
    Task<IReadOnlyList<StuckRequest>> ListStuckAsync(TimeSpan olderThan, CancellationToken ct = default);
}

/// One stuck request. CommandStatus is the command row's status word, null when no row carries the request's key.
public record StuckRequest(
    string Resource,
    Guid Id,
    Guid TenantId,
    Guid RequestId,
    string Kind,
    DateTimeOffset AskedAt,
    string? CommandStatus)
{
    /// Pending or Leased: the worker has not finished it. Anything else: no command will report this request.
    public bool CommandIsOpen => CommandStatus is "Pending" or "Leased";
}
