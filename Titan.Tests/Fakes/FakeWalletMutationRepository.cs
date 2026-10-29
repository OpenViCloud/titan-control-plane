using Titan.Domain.Aggregates.Billing.WalletMutation;
using Titan.Domain.Repositories.Billing;

namespace Titan.Tests.Fakes;

/// titan-ops architecture/money-and-wallet.md#single-writer-outbox — an in-memory mutation outbox for service-level tests: it records what an enqueuer
/// staged, so a test can assert on the mutation that WOULD be written without a database.
///
/// It holds one row per (ref_type, ref_id), as Postgres does: a second Add for the same source throws instead of
/// recording a state the database cannot hold (a fake without that rule hid MONEY-BUGS M-2). It does not model
/// the collision error a racing writer gets; those races are proven on Postgres (InvoiceDebitRetryDbTests).
public sealed class FakeWalletMutationRepository : IWalletMutationRepository
{
    public List<WalletMutation> Added { get; } = [];

    /// Mutations this fake will hand back to a payment-state lookup, keyed by (refType, refId).
    public Dictionary<(string RefType, Guid RefId), WalletMutation> ByRef { get; } = [];

    // A staged mutation is also what a later payment-state lookup finds, the way the real repository
    // returns it once committed. The pay commands return nothing (titan-ops architecture/http-contract.md#commands-return-no-data), so a
    // test reads the invoice back and must see the mutation it just caused — one row per ref.
    public void Add(WalletMutation mutation)
    {
        bool refHasARow = ByRef.ContainsKey((mutation.RefType, mutation.RefId));
        if (refHasARow)
        {
            throw new InvalidOperationException(
                $"{mutation.RefType} {mutation.RefId} already has a mutation row; UNIQUE(ref_type, ref_id) " +
                "would refuse a second one.");
        }

        Added.Add(mutation);
        ByRef[(mutation.RefType, mutation.RefId)] = mutation;
    }

    public Task<WalletMutation?> GetForUpdateAsync(Guid mutationId, CancellationToken ct) =>
        Task.FromResult(Added.FirstOrDefault(c => c.Id == mutationId));

    public Task<WalletMutation?> GetForRefAsync(string refType, Guid refId, CancellationToken ct) =>
        Task.FromResult(ByRef.GetValueOrDefault((refType, refId)));

    public Task<WalletMutation?> GetForRefForUpdateAsync(string refType, Guid refId, CancellationToken ct) =>
        Task.FromResult(ByRef.GetValueOrDefault((refType, refId)));

    public Task<IReadOnlyDictionary<Guid, WalletMutation>> GetForRefsAsync(
        string refType, IReadOnlyCollection<Guid> refIds, CancellationToken ct)
    {
        var hits = ByRef
            .Where(kv => kv.Key.RefType == refType && refIds.Contains(kv.Key.RefId))
            .ToDictionary(kv => kv.Key.RefId, kv => kv.Value);

        return Task.FromResult<IReadOnlyDictionary<Guid, WalletMutation>>(hits);
    }

    /// The sweeper's drain is FOR UPDATE SKIP LOCKED raw SQL — there is nothing honest to fake about
    /// row locking in a List, so the sweeper's tests run against Postgres (Category=Integration).
    public Task<IReadOnlyList<WalletMutation>> ListPendingOlderThanAsync(
        DateTimeOffset cutoff, int batchSize, CancellationToken ct) => throw new NotSupportedException(
            "The sweeper drain is proven against Postgres (FOR UPDATE SKIP LOCKED has no in-memory meaning).");

    /// The health snapshot, computed off the same in-memory list the rest of this fake serves,
    /// so a test that stages mutations gets a consistent answer here without a second setup step.
    /// Poison mirrors the real query: Failed AND attempts exhausted, never status alone.
    public Task<WalletMutationBacklog> GetHealthSnapshotAsync(
        DateTimeOffset now, int maxAttempts, CancellationToken ct)
    {
        var poison = Added.Count(c => c.Status == WalletMutationStatus.Failed && c.Attempts >= maxAttempts);

        var pending = Added
            .Where(c => c.Status == WalletMutationStatus.Pending)
            .OrderBy(c => c.QueuedAt)
            .ToList();

        var oldest = pending.Count == 0 ? (TimeSpan?)null : now - pending[0].QueuedAt;
        return Task.FromResult(new WalletMutationBacklog(poison, pending.Count, oldest));
    }
}
