using Microsoft.EntityFrameworkCore;
using Titan.Infrastructure.Hosting;
using Titan.Infrastructure.Persistence;

namespace Titan.Tests.Infrastructure;

// /healthz/reconcilers answers the same on every control plane replica: a poll that reached one replica is seen by the
// health check of another, because the polls live in core.reconciler_polls (titan-ops architecture/platform-infra.md#control-plane-replicas).
public class ReconcilerHeartbeatTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 12, 0, 0, 0, TimeSpan.Zero);

    private static TitanDbContext Replica(string database) =>
        new(new DbContextOptionsBuilder<TitanDbContext>().UseInMemoryDatabase(database).Options);

    [Fact]
    public async Task APollOnOneReplica_IsSeenByAnother()
    {
        string database = $"reconciler-polls-{Guid.NewGuid()}";
        await using TitanDbContext first = Replica(database);
        await using TitanDbContext second = Replica(database);

        await new ReconcilerHeartbeat(first).RecordPollAsync(ReconcilerHeartbeat.KeyFor(ReconcilerHeartbeat.VmKind, "mock"), T0, CancellationToken.None);

        IReadOnlyDictionary<string, DateTimeOffset> seen = await new ReconcilerHeartbeat(second).SnapshotAsync(CancellationToken.None);
        Assert.Equal(T0, seen["vms/mock"]);
    }

    [Fact]
    public async Task ALaterPoll_MovesTheTime_AndKeepsOneRow()
    {
        string database = $"reconciler-polls-{Guid.NewGuid()}";
        await using (TitanDbContext db = Replica(database))
        {
            await new ReconcilerHeartbeat(db).RecordPollAsync("networks/mock", T0, CancellationToken.None);
        }
        await using (TitanDbContext db = Replica(database))
        {
            await new ReconcilerHeartbeat(db).RecordPollAsync("networks/mock", T0.AddMinutes(2), CancellationToken.None);
        }

        await using TitanDbContext read = Replica(database);
        Assert.Equal(T0.AddMinutes(2), (await new ReconcilerHeartbeat(read).SnapshotAsync(CancellationToken.None))["networks/mock"]);
        Assert.Equal(1, await read.ReconcilerPolls.CountAsync());
    }
}
