using Microsoft.EntityFrameworkCore;
using Titan.Domain.Aggregates.ProviderCommand;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.Repositories;

namespace Titan.Tests.Infrastructure;

// The claim query is the SQL of ProviderCommand.IsClaimable (titan-ops architecture/domain-methods.md#plain-code): every
// shape a row can be in goes through both, so the two cannot drift apart.
//
// Silent about: Postgres-only behaviour (InMemory runs the same LINQ, not the SQL EF generates from it); the race of two
// workers on one row is ProviderCommandLeaseDbTests.
public class ProviderCommandRepositoryStateTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    private static TitanDbContext NewDb() =>
        new(new DbContextOptionsBuilder<TitanDbContext>()
            .UseInMemoryDatabase($"provider-command-state-{Guid.NewGuid()}")
            .Options);

    // Each shape in a tenant of its own: the lane of a tenant is measured apart, below.
    private static ProviderCommand Pending(string key, DateTimeOffset createdAt, Guid? tenantId = null,
        string kind = ProviderCommandKinds.VmDelete) =>
        ProviderCommand.Create(key, kind, "mock", "{}", createdAt, tenantId ?? Guid.NewGuid());

    private static ProviderCommand Leased(string key, DateTimeOffset leasedAt)
    {
        ProviderCommand row = Pending(key, leasedAt);
        row.Lease(Guid.NewGuid(), leasedAt);
        return row;
    }

    // Every shape, named by its key; the key is what the assertions list.
    private static List<ProviderCommand> EveryShape()
    {
        ProviderCommand done = Leased("done", Now.AddHours(-1));
        done.Complete(done.LeaseId!.Value);
        ProviderCommand failed = Leased("failed", Now.AddHours(-1));
        failed.Fail(failed.LeaseId!.Value, "gave up", retryable: false, Now.AddHours(-1));
        ProviderCommand retryWaiting = Leased("retry-waiting", Now);
        retryWaiting.Fail(retryWaiting.LeaseId!.Value, "try again", retryable: true, Now);

        return
        [
            Pending("pending-due", Now.AddMinutes(-1)),
            Pending("pending-now", Now),
            Pending("pending-later", Now.AddMinutes(5)),
            Leased("lease-ran-out", Now.AddHours(-1)),
            Leased("lease-runs-out-now", Now - ProviderCommand.LeaseLength),
            Leased("lease-held", Now),
            done, failed, retryWaiting,
        ];
    }

    [Fact]
    public async Task Claimable_IsAPendingRowThatIsDue_OrALeaseThatRanOut()
    {
        await using TitanDbContext db = NewDb();
        db.ProviderCommands.AddRange(EveryShape());
        await db.SaveChangesAsync();

        var claimable = await new ProviderCommandRepository(db).ListClaimableAsync([ProviderCommandKinds.VmDelete], Now, 100, default);

        Assert.Equal(["lease-ran-out", "lease-runs-out-now", "pending-due", "pending-now"],
            claimable.Select(c => c.Key).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ClaimQuery_AgreesWithIsClaimable_ForEveryShape()
    {
        await using TitanDbContext db = NewDb();
        List<ProviderCommand> shapes = EveryShape();
        db.ProviderCommands.AddRange(shapes);
        await db.SaveChangesAsync();

        var claimable = await new ProviderCommandRepository(db).ListClaimableAsync([ProviderCommandKinds.VmDelete], Now, 100, default);

        Assert.Equal(shapes.Where(c => c.IsClaimable(Now)).Select(c => c.Key).Order(StringComparer.Ordinal),
            claimable.Select(c => c.Key).Order(StringComparer.Ordinal));
    }

    private static readonly Guid Busy = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Idle = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    // Lanes: a busy tenant (a held lease, a pending row, a pending backup: its backup lane is free), a busy no-tenant
    // lane, a tenant whose backup lane is held but its command lane is free, and a tenant whose only leased row ran out
    // (that row is the one handed out).
    private static List<ProviderCommand> EveryLane()
    {
        ProviderCommand ranOut = Leased("ran-out-tenant-leased", Now.AddHours(-1));
        Guid ranOutTenant = ranOut.TenantId!.Value;
        ProviderCommand syncHeld = ProviderCommand.Create("sync-held", ProviderCommandKinds.TemplateSync, "mock", "{}", Now);
        syncHeld.Lease(Guid.NewGuid(), Now);
        ProviderCommand busyHeld = Pending("busy-held", Now.AddMinutes(-2), Busy);
        busyHeld.Lease(Guid.NewGuid(), Now);
        ProviderCommand idleBackupHeld = Pending("idle-backup-held", Now.AddMinutes(-2), Idle, ProviderCommandKinds.VmBackup);
        idleBackupHeld.Lease(Guid.NewGuid(), Now);
        return
        [
            busyHeld,
            Pending("busy-waiting", Now.AddMinutes(-1), Busy),
            Pending("busy-backup", Now.AddMinutes(-1), Busy, ProviderCommandKinds.VmBackup),
            syncHeld,
            ProviderCommand.Create("sync-waiting", ProviderCommandKinds.TemplateSync, "mock", "{}", Now.AddMinutes(-1)),
            idleBackupHeld,
            Pending("idle-waiting", Now.AddMinutes(-1), Idle),
            ranOut,
            Pending("ran-out-tenant-waiting", Now.AddMinutes(-1), ranOutTenant),
        ];
    }

    private static readonly string[] CommandKinds =
        [ProviderCommandKinds.VmDelete, ProviderCommandKinds.TemplateSync, ProviderCommandKinds.VmBackup];

    [Fact]
    public async Task Claimable_SkipsALaneThatHoldsALease_ButNotTheLeaseThatRanOut()
    {
        await using TitanDbContext db = NewDb();
        db.ProviderCommands.AddRange(EveryLane());
        await db.SaveChangesAsync();

        var claimable = await new ProviderCommandRepository(db).ListClaimableAsync(CommandKinds, Now, 100, default);

        Assert.Equal(["busy-backup", "idle-waiting", "ran-out-tenant-leased"],
            claimable.Select(c => c.Key).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ClaimQuery_AgreesWithHoldsTheLaneOf_ForEveryLane()
    {
        await using TitanDbContext db = NewDb();
        List<ProviderCommand> lanes = EveryLane();
        db.ProviderCommands.AddRange(lanes);
        await db.SaveChangesAsync();

        var claimable = await new ProviderCommandRepository(db).ListClaimableAsync(CommandKinds, Now, 100, default);

        IEnumerable<string> expected = lanes
            .Where(c => CommandKinds.Contains(c.Kind) && c.IsClaimable(Now))
            .Where(c => !lanes.Any(other => CommandKinds.Contains(other.Kind) && other.HoldsTheLaneOf(c)))
            .Select(c => c.Key);
        Assert.Equal(expected.Order(StringComparer.Ordinal), claimable.Select(c => c.Key).Order(StringComparer.Ordinal));
    }
}
