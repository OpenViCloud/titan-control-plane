using Microsoft.EntityFrameworkCore;
using Titan.Infrastructure.Hosting;
using Titan.Infrastructure.Persistence;

namespace Titan.Tests.Infrastructure.Integration;

// Replicas recording the first poll of one reconciler at once: one row, and no poll fails — a list request must not
// turn into a 500 because another replica inserted the row first (titan-ops architecture/provider-and-worker.md#reconciler-is-a-worker-ticker).
[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public class ReconcilerHeartbeatDbTests(PostgresFixture pg)
{
    private const int Replicas = 8;

    [Fact]
    public async Task ReplicasRecordingTheFirstPollAtOnce_LeaveOneRow_AndNoneFails()
    {
        string key = ReconcilerHeartbeat.KeyFor(ReconcilerHeartbeat.VmKind, "c-" + Guid.NewGuid().ToString("N"));
        using var start = new SemaphoreSlim(0, Replicas);

        Task<Exception?>[] polls = Enumerable.Range(0, Replicas).Select(async i =>
        {
            await using TitanDbContext db = pg.NewDbContext();
            await start.WaitAsync();
            try
            {
                await new ReconcilerHeartbeat(db).RecordPollAsync(key, DateTimeOffset.UtcNow, CancellationToken.None);
                return null;
            }
            catch (Exception e)
            {
                return e;
            }
        }).ToArray();
        start.Release(Replicas);
        Exception?[] failures = await Task.WhenAll(polls);

        Assert.All(failures, Assert.Null);
        await using TitanDbContext read = pg.NewDbContext();
        Assert.Equal(1, await read.ReconcilerPolls.CountAsync(p => p.Key == key));
    }
}
