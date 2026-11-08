using Titan.Domain.Aggregates.LoadBalancer;
using Titan.Domain.Exceptions;

namespace Titan.Tests.Domain;

// LoadBalancerOperation is what a load balancer keeps about the operation it waits on, the last one that failed and whether Incus
// was seen holding the load balancer while one waits (titan-ops features/load-balancer/design.md#lb-lifecycle). Every move returns an
// immutable object and leaves the old one as it was.
public class LoadBalancerOperationTests
{
    private static readonly DateTimeOffset AskedAt = new(2026, 11, 7, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FailedAt = new(2026, 11, 7, 11, 0, 0, TimeSpan.Zero);

    [Fact]
    public void None_HoldsNothing()
    {
        LoadBalancerOperation none = LoadBalancerOperation.None;

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

        LoadBalancerOperation asked = LoadBalancerOperation.None.Asked(id, AskedAt, LoadBalancerStatus.Available);

        Assert.Equal(id, asked.PendingId);
        Assert.Equal(AskedAt, asked.AskedAt);
        Assert.Equal(LoadBalancerStatus.Available, asked.LastSeenStatus);
    }

    [Fact]
    public void Asked_KeepsTheLastFailure_SoItShowsUntilAnOperationEndsDone()
    {
        LoadBalancerOperation failed = LoadBalancerOperation.None.Asked(Guid.NewGuid(), AskedAt, null).Failed("NO_ROOM", "raw", FailedAt);

        LoadBalancerOperation again = failed.Asked(Guid.NewGuid(), AskedAt, null);

        Assert.Equal("NO_ROOM", again.FailureReason);
        Assert.Equal("raw", again.FailureDetail);
        Assert.Equal(FailedAt, again.FailedAt);
    }

    [Fact]
    public void Asked_WithAnEmptyId_IsRefused_ForNothingCouldEndIt()
    {
        var thrown = Assert.Throws<BusinessException>(() => LoadBalancerOperation.None.Asked(Guid.Empty, AskedAt, null));

        Assert.Equal("INVALID_REQUEST", thrown.Code);
    }

    [Fact]
    public void Saw_RecordsTheStatusSeen_AndKeepsTheOperation()
    {
        Guid id = Guid.NewGuid();
        LoadBalancerOperation asked = LoadBalancerOperation.None.Asked(id, AskedAt, null);

        LoadBalancerOperation seen = asked.Saw(LoadBalancerStatus.Available);

        Assert.Equal(LoadBalancerStatus.Available, seen.LastSeenStatus);
        Assert.Equal(id, seen.PendingId);
        Assert.Equal(AskedAt, seen.AskedAt);
        Assert.Null(seen.Saw(null).LastSeenStatus);
    }

    [Fact]
    public void Done_ForgetsTheOperationTheStatusSeenAndTheLastFailure()
    {
        LoadBalancerOperation failed = LoadBalancerOperation.None.Asked(Guid.NewGuid(), AskedAt, null).Failed("r", "d", FailedAt);
        LoadBalancerOperation asked = failed.Asked(Guid.NewGuid(), AskedAt, LoadBalancerStatus.Available);

        Assert.Equal(LoadBalancerOperation.None, asked.Done());
    }

    [Fact]
    public void Failed_RecordsTheFailure_AndNothingWaitsAnyMore_NorIsAStatusHeld()
    {
        LoadBalancerOperation asked = LoadBalancerOperation.None.Asked(Guid.NewGuid(), AskedAt, LoadBalancerStatus.Available);

        LoadBalancerOperation failed = asked.Failed("NO_ROOM", "raw", FailedAt);

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
        LoadBalancerOperation first = LoadBalancerOperation.None.Asked(Guid.NewGuid(), AskedAt, null).Failed("OLD", "old", AskedAt);

        LoadBalancerOperation second = first.Asked(Guid.NewGuid(), AskedAt, null).Failed("NEW", null, FailedAt);

        Assert.Equal("NEW", second.FailureReason);
        Assert.Null(second.FailureDetail);
        Assert.Equal(FailedAt, second.FailedAt);
    }

    [Fact]
    public void ReturnedToRest_ForgetsTheOperationAndTheStatusSeen_ButKeepsTheLastFailure()
    {
        LoadBalancerOperation asked = LoadBalancerOperation.None.Asked(Guid.NewGuid(), AskedAt, null).Failed("r", "d", FailedAt)
            .Asked(Guid.NewGuid(), AskedAt, LoadBalancerStatus.Available);

        LoadBalancerOperation rested = asked.ReturnedToRest();

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
        LoadBalancerOperation asked = LoadBalancerOperation.None.Asked(id, AskedAt, LoadBalancerStatus.Available);

        asked.Failed("r", "d", FailedAt);
        asked.Done();
        asked.ReturnedToRest();
        asked.Saw(null);

        Assert.Equal(id, asked.PendingId);
        Assert.Equal(LoadBalancerStatus.Available, asked.LastSeenStatus);
        Assert.Null(asked.FailureReason);
    }

    [Fact]
    public void TwoObjectsWithTheSameFields_AreEqual()
    {
        Guid id = Guid.NewGuid();

        Assert.Equal(
            LoadBalancerOperation.None.Asked(id, AskedAt, LoadBalancerStatus.Available),
            LoadBalancerOperation.None.Asked(id, AskedAt, LoadBalancerStatus.Available));
    }

    [Fact]
    public void NoPropertyOfItIsPubliclyWritable()
    {
        foreach (var property in typeof(LoadBalancerOperation).GetProperties())
        {
            Assert.False(property.SetMethod != null && property.SetMethod.IsPublic, property.Name);
        }
    }
}
