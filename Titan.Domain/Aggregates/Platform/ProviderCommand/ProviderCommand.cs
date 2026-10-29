using Titan.Domain.Exceptions;

namespace Titan.Domain.Aggregates.Platform.ProviderCommand;

/// One row of the provider command table (titan-ops architecture/provider-and-worker.md#provider-command-table): a handler
/// writes it in the same SaveChanges as the aggregate it changed, and a worker leases it over /internal.
public class ProviderCommand
{
    public const int MaxAttempts = 5;
    public static readonly TimeSpan LeaseLength = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan FirstBackoff = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(2);

    public Guid Id { get; private set; }

    /// UNIQUE: the old workflow id (`vm-provision-{vmId}`, `vm-stop-{vmId}-{intentId}`, …). One key, one row, reused
    /// by a repeat according to Rerun.
    public string Key { get; private set; } = default!;

    /// What the worker does: `vm.create`, `vm.delete`, … (ProviderCommandKinds).
    public string Kind { get; private set; } = default!;

    public string ClusterId { get; private set; } = default!;

    /// JSON the worker reads; its property names are the wire format.
    public string Payload { get; private set; } = default!;

    public ProviderCommandRerun Rerun { get; private set; }
    public ProviderCommandStatus Status { get; private set; }

    /// Counts leases taken for the current run; a repeat that reopens the row starts it at 0 again.
    public int Attempts { get; private set; }

    /// A Pending row is not handed out before this: the backoff after a retryable failure.
    public DateTimeOffset AvailableAt { get; private set; }

    /// Minted by the worker; every later call of that worker names it.
    public Guid? LeaseId { get; private set; }

    public DateTimeOffset? LeaseUntil { get; private set; }
    public string? LastError { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? FinishedAt { get; private set; }

    /// Concurrency token: two workers leasing the same row at once both read version N; the second write matches 0 rows.
    public int Version { get; private set; }

    private ProviderCommand() { }

    public static ProviderCommand Create(
        string key, string kind, string clusterId, string payload, ProviderCommandRerun rerun, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("A provider command needs a key.", nameof(key));
        if (string.IsNullOrWhiteSpace(kind))
            throw new ArgumentException("A provider command needs a kind.", nameof(kind));

        return new ProviderCommand
        {
            Id = Guid.NewGuid(),
            Key = key,
            Kind = kind,
            ClusterId = clusterId,
            Payload = payload,
            Rerun = rerun,
            Status = ProviderCommandStatus.Pending,
            Attempts = 0,
            AvailableAt = now,
            LeaseId = null,
            LeaseUntil = null,
            LastError = null,
            CreatedAt = now,
            FinishedAt = null,
            Version = 0
        };
    }

    public bool IsOpen => Status is ProviderCommandStatus.Pending or ProviderCommandStatus.Leased;

    /// A handler enqueued this key again. Returns true when the row runs again (with the new payload), false when the
    /// repeat is absorbed. Not an intent of the aggregate: the decision is the Rerun rule, applied the same every time.
    public bool Reopen(string payload, DateTimeOffset now)
    {
        if (IsOpen)
        {
            return false;
        }
        bool doneAndOnlyFailuresRerun =
            Status == ProviderCommandStatus.Done && Rerun == ProviderCommandRerun.AfterFailureOnly;
        if (doneAndOnlyFailuresRerun)
        {
            return false;
        }
        Payload = payload;
        Status = ProviderCommandStatus.Pending;
        Attempts = 0;
        AvailableAt = now;
        LeaseId = null;
        LeaseUntil = null;
        LastError = null;
        FinishedAt = null;
        Version++;
        return true;
    }

    public bool IsClaimable(DateTimeOffset now)
    {
        bool pendingAndDue = Status == ProviderCommandStatus.Pending && AvailableAt <= now;
        bool leaseRanOut = Status == ProviderCommandStatus.Leased && LeaseUntil != null && LeaseUntil.Value <= now;
        return pendingAndDue || leaseRanOut;
    }

    public void Lease(Guid leaseId, DateTimeOffset now)
    {
        if (!IsClaimable(now))
            throw new ConflictException("PROVIDER_COMMAND_NOT_CLAIMABLE",
                $"Provider command {Id} is {Status} and cannot be leased now.");
        Status = ProviderCommandStatus.Leased;
        LeaseId = leaseId;
        LeaseUntil = now + LeaseLength;
        Attempts++;
        Version++;
    }

    public void ExtendLease(Guid leaseId, DateTimeOffset now)
    {
        if (!IsHeldBy(leaseId))
            throw LeaseLost(leaseId);
        LeaseUntil = now + LeaseLength;
        Version++;
    }

    /// The worker finished the provider work. Accepted from the lease that did it even when the lease ran out, as long
    /// as no other lease took the row since: the work happened.
    public void Complete(Guid leaseId, DateTimeOffset now)
    {
        if (!IsHeldBy(leaseId))
            throw LeaseLost(leaseId);
        Status = ProviderCommandStatus.Done;
        LeaseUntil = null;
        FinishedAt = now;
        Version++;
    }

    /// A retryable failure goes back to Pending after a backoff (5 s doubling, at most 2 min) until MaxAttempts
    /// leases were taken; then, or for a failure the worker calls final, the row is Failed. The worker reports the
    /// give-up to the resource itself (its give-up callback) before calling this.
    public void Fail(Guid leaseId, string error, bool retryable, DateTimeOffset now)
    {
        if (!IsHeldBy(leaseId))
            throw LeaseLost(leaseId);
        LastError = error;
        LeaseUntil = null;
        Version++;
        bool attemptsLeft = Attempts < MaxAttempts;
        if (retryable && attemptsLeft)
        {
            Status = ProviderCommandStatus.Pending;
            AvailableAt = now + BackoffAfter(Attempts);
            return;
        }
        Status = ProviderCommandStatus.Failed;
        FinishedAt = now;
    }

    public static TimeSpan BackoffAfter(int attempts)
    {
        TimeSpan backoff = FirstBackoff;
        for (int i = 1; i < attempts; i++)
        {
            backoff += backoff;
            if (backoff >= MaxBackoff)
            {
                return MaxBackoff;
            }
        }
        return backoff;
    }

    public bool IsHeldBy(Guid leaseId) => Status == ProviderCommandStatus.Leased && LeaseId == leaseId;

    private ConflictException LeaseLost(Guid leaseId) =>
        new("PROVIDER_COMMAND_LEASE_LOST", $"Lease {leaseId} does not hold provider command {Id}.");
}
