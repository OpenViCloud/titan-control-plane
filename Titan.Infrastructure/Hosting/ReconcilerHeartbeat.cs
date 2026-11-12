using Microsoft.EntityFrameworkCore;
using Npgsql;
using Titan.Infrastructure.Persistence;

namespace Titan.Infrastructure.Hosting;

/// <summary>
/// Keyed "{kind}/{clusterId}" (e.g. "networks/vn-hcm-1"). A reconciler that died raises no error, its resources
/// just stop changing; this turns that silence into a /healthz/reconcilers signal. A list request proves the
/// loop reaches the control plane, not that the status reports that follow succeed. The polls are rows of
/// core.reconciler_polls, so the answer does not depend on which replica the worker or the probe reached.
/// </summary>
public sealed class ReconcilerHeartbeat(TitanDbContext db)
{
    public const string VmKind = "vms";
    public const string NetworkKind = "networks";
    public const string VolumeKind = "volumes";
    public const string FirewallKind = "firewall-rules";
    public const string VmSnapshotKind = "vm-snapshots";
    public const string TemplateKind = "templates";
    public const string LoadBalancerKind = "load-balancers";

    public async Task RecordPollAsync(string key, DateTimeOffset at, CancellationToken ct)
    {
        ReconcilerPoll? poll = await db.ReconcilerPolls.FirstOrDefaultAsync(p => p.Key == key, ct);
        if (poll != null)
        {
            poll.PolledAt = at;
            await db.SaveChangesAsync(ct);
            return;
        }
        var first = new ReconcilerPoll { Key = key, PolledAt = at };
        db.ReconcilerPolls.Add(first);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Another replica recorded this reconciler's first poll a moment ago: the poll is recorded.
            db.Entry(first).State = EntityState.Detached;
        }
    }

    public async Task<IReadOnlyDictionary<string, ReconcilerPollState>> SnapshotAsync(CancellationToken ct)
    {
        List<ReconcilerPoll> polls = await db.ReconcilerPolls.AsNoTracking().ToListAsync(ct);
        return polls.ToDictionary(p => p.Key, p => new ReconcilerPollState(RegisteredAt: p.PolledAt, LastPollAt: p.PolledAt));
    }

    public static string KeyFor(string kind, string clusterId)
    {
        return $"{kind}/{clusterId}";
    }
}

/// <param name="RegisteredAt">When the reconciler was first expected — the reference for the boot grace window.</param>
/// <param name="LastPollAt">Last time it asked the control plane what to observe; null until the first time.</param>
public sealed record ReconcilerPollState(DateTimeOffset RegisteredAt, DateTimeOffset? LastPollAt);
