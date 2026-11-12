namespace Titan.Application.Abstractions;

/// One pass of a named background job at a time across every control plane pod
/// (titan-ops architecture/background-jobs.md#background-jobs).
public interface IJobPassLock
{
    /// Waits until no other pass of the job holds the lock, then holds it until the scope's next save commits, or the
    /// scope ends. Taken before the pass reads anything, so a pass reads what the one before it saved.
    Task TakeAsync(string job, CancellationToken ct);
}
