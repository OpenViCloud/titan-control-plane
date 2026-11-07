using Titan.Domain.Aggregates.Network;
using Titan.Domain.Exceptions;

namespace Titan.Tests.Domain;

// FirewallRuleOperation is what a firewall rule keeps about the operation it waits on, the last one that failed and the Incus state
// seen while one waits (titan-ops features/network/design.md#network-operation). Every move returns an immutable object and
// leaves the old one as it was.
public class FirewallRuleOperationTests
{
    private static readonly DateTimeOffset AskedAt = new(2026, 11, 7, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FailedAt = new(2026, 11, 7, 11, 0, 0, TimeSpan.Zero);

    [Fact]
    public void None_HoldsNothing()
    {
        FirewallRuleOperation none = FirewallRuleOperation.None;

        Assert.Null(none.PendingId);
        Assert.Null(none.AskedAt);
        Assert.Null(none.FailureReason);
        Assert.Null(none.FailureDetail);
        Assert.Null(none.FailedAt);
        Assert.Null(none.LastSeenStatus);
    }

    [Fact]
    public void Asked_HoldsTheOperationWhenItWasAskedAndTheStatusSeen()
    {
        Guid id = Guid.NewGuid();

        FirewallRuleOperation asked = FirewallRuleOperation.None.Asked(id, AskedAt, FirewallRuleStatus.enabled);

        Assert.Equal(id, asked.PendingId);
        Assert.Equal(AskedAt, asked.AskedAt);
        Assert.Equal(FirewallRuleStatus.enabled, asked.LastSeenStatus);
    }

    [Fact]
    public void Asked_KeepsTheLastFailure_SoItShowsUntilAnOperationEndsDone()
    {
        FirewallRuleOperation failed = FirewallRuleOperation.None.Asked(Guid.NewGuid(), AskedAt, null).Failed("NO_ROOM", "raw", FailedAt);

        FirewallRuleOperation again = failed.Asked(Guid.NewGuid(), AskedAt, null);

        Assert.Equal("NO_ROOM", again.FailureReason);
        Assert.Equal("raw", again.FailureDetail);
        Assert.Equal(FailedAt, again.FailedAt);
    }

    [Fact]
    public void Asked_WithAnEmptyId_IsRefused_ForNothingCouldEndIt()
    {
        var thrown = Assert.Throws<BusinessException>(() => FirewallRuleOperation.None.Asked(Guid.Empty, AskedAt, null));

        Assert.Equal("INVALID_REQUEST", thrown.Code);
    }

    [Fact]
    public void Saw_RecordsTheStatusSeen_AndKeepsTheOperation()
    {
        Guid id = Guid.NewGuid();
        FirewallRuleOperation asked = FirewallRuleOperation.None.Asked(id, AskedAt, null);

        FirewallRuleOperation seen = asked.Saw(FirewallRuleStatus.enabled);

        Assert.Equal(FirewallRuleStatus.enabled, seen.LastSeenStatus);
        Assert.Equal(id, seen.PendingId);
        Assert.Equal(AskedAt, seen.AskedAt);
        Assert.Null(seen.Saw(null).LastSeenStatus);
    }

    [Fact]
    public void Done_ForgetsTheOperationTheStatusSeenAndTheLastFailure()
    {
        FirewallRuleOperation failed = FirewallRuleOperation.None.Asked(Guid.NewGuid(), AskedAt, null).Failed("r", "d", FailedAt);
        FirewallRuleOperation asked = failed.Asked(Guid.NewGuid(), AskedAt, FirewallRuleStatus.enabled);

        Assert.Equal(FirewallRuleOperation.None, asked.Done());
    }

    [Fact]
    public void Failed_RecordsTheFailure_AndNothingWaitsAnyMore_NorIsAStatusHeld()
    {
        FirewallRuleOperation asked = FirewallRuleOperation.None.Asked(Guid.NewGuid(), AskedAt, FirewallRuleStatus.enabled);

        FirewallRuleOperation failed = asked.Failed("NO_ROOM", "raw", FailedAt);

        Assert.Null(failed.PendingId);
        Assert.Null(failed.AskedAt);
        Assert.Null(failed.LastSeenStatus);
        Assert.Equal("NO_ROOM", failed.FailureReason);
        Assert.Equal("raw", failed.FailureDetail);
        Assert.Equal(FailedAt, failed.FailedAt);
    }

    [Fact]
    public void Failed_ReplacesAnOlderFailure()
    {
        FirewallRuleOperation first = FirewallRuleOperation.None.Asked(Guid.NewGuid(), AskedAt, null).Failed("OLD", "old", AskedAt);

        FirewallRuleOperation second = first.Asked(Guid.NewGuid(), AskedAt, null).Failed("NEW", null, FailedAt);

        Assert.Equal("NEW", second.FailureReason);
        Assert.Null(second.FailureDetail);
        Assert.Equal(FailedAt, second.FailedAt);
    }

    [Fact]
    public void ReturnedToRest_ForgetsTheOperationAndTheStatusSeen_ButKeepsTheLastFailure()
    {
        FirewallRuleOperation asked = FirewallRuleOperation.None.Asked(Guid.NewGuid(), AskedAt, null).Failed("r", "d", FailedAt)
            .Asked(Guid.NewGuid(), AskedAt, FirewallRuleStatus.enabled);

        FirewallRuleOperation rested = asked.ReturnedToRest();

        Assert.Null(rested.PendingId);
        Assert.Null(rested.AskedAt);
        Assert.Null(rested.LastSeenStatus);
        Assert.Equal("r", rested.FailureReason);
        Assert.Equal("d", rested.FailureDetail);
        Assert.Equal(FailedAt, rested.FailedAt);
    }

    [Fact]
    public void EveryMove_LeavesTheOldObjectAsItWas()
    {
        Guid id = Guid.NewGuid();
        FirewallRuleOperation asked = FirewallRuleOperation.None.Asked(id, AskedAt, FirewallRuleStatus.enabled);

        asked.Failed("r", "d", FailedAt);
        asked.Done();
        asked.ReturnedToRest();
        asked.Saw(null);

        Assert.Equal(id, asked.PendingId);
        Assert.Equal(FirewallRuleStatus.enabled, asked.LastSeenStatus);
        Assert.Null(asked.FailureReason);
    }
}
