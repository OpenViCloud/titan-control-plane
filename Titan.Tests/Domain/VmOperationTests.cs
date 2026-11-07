using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Exceptions;

namespace Titan.Tests.Domain;

// VmOperation is what a VM keeps about the operation it waits on, the last one that failed, and the Incus status seen
// while it waits (titan-ops features/vm/design.md#vm-status). Every move returns an immutable object and leaves the old one as it was.
public class VmOperationTests
{
    private static readonly DateTimeOffset Asked = new(2026, 11, 6, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Failed = new(2026, 11, 6, 11, 0, 0, TimeSpan.Zero);

    [Fact]
    public void None_HoldsNothing()
    {
        VmOperation none = VmOperation.None;

        Assert.Null(none.PendingId);
        Assert.Null(none.AskedAt);
        Assert.Null(none.FailureReason);
        Assert.Null(none.FailureDetail);
        Assert.Null(none.FailedAt);
        Assert.Null(none.LastSeenStatus);
    }

    [Fact]
    public void Asked_HoldsTheOperationAndTheStatusSeenWhenItWasAsked()
    {
        Guid id = Guid.NewGuid();

        VmOperation asked = VmOperation.None.Asked(id, Asked, VmStatus.Stopped);

        Assert.Equal(id, asked.PendingId);
        Assert.Equal(Asked, asked.AskedAt);
        Assert.Equal(VmStatus.Stopped, asked.LastSeenStatus);
    }

    [Fact]
    public void Asked_KeepsTheLastFailure_SoItShowsUntilAnOperationEndsDone()
    {
        VmOperation failed = VmOperation.None.Asked(Guid.NewGuid(), Asked, null).Failed("NO_ROOM", "raw", Failed);

        VmOperation again = failed.Asked(Guid.NewGuid(), Asked, VmStatus.Running);

        Assert.Equal("NO_ROOM", again.FailureReason);
        Assert.Equal("raw", again.FailureDetail);
        Assert.Equal(Failed, again.FailedAt);
    }

    [Fact]
    public void Asked_WithAnEmptyId_IsRefused_ForNothingCouldEndIt()
    {
        var thrown = Assert.Throws<BusinessException>(() => VmOperation.None.Asked(Guid.Empty, Asked, null));

        Assert.Equal("INVALID_REQUEST", thrown.Code);
    }

    [Fact]
    public void Asked_WithNoId_IsASnapshot_ItHoldsTheStatusAndNoOperation()
    {
        VmOperation snapshotting = VmOperation.None.Asked(null, null, VmStatus.Frozen);

        Assert.Null(snapshotting.PendingId);
        Assert.Null(snapshotting.AskedAt);
        Assert.Equal(VmStatus.Frozen, snapshotting.LastSeenStatus);
    }

    [Fact]
    public void Saw_ChangesOnlyTheStatusSeen()
    {
        Guid id = Guid.NewGuid();
        VmOperation asked = VmOperation.None.Asked(id, Asked, VmStatus.Running);

        VmOperation saw = asked.Saw(VmStatus.Stopped);

        Assert.Equal(VmStatus.Stopped, saw.LastSeenStatus);
        Assert.Equal(id, saw.PendingId);
        Assert.Equal(Asked, saw.AskedAt);
        Assert.Null(saw.Saw(null).LastSeenStatus);
    }

    [Fact]
    public void Done_ForgetsTheOperation_TheStatusSeen_AndTheLastFailure()
    {
        VmOperation failed = VmOperation.None.Asked(Guid.NewGuid(), Asked, null).Failed("r", "d", Failed);
        VmOperation asked = failed.Asked(Guid.NewGuid(), Asked, VmStatus.Running);

        Assert.Equal(VmOperation.None, asked.Done());
    }

    [Fact]
    public void Failed_RecordsTheFailure_AndForgetsTheOperationAndTheStatusSeen()
    {
        VmOperation asked = VmOperation.None.Asked(Guid.NewGuid(), Asked, VmStatus.Running);

        VmOperation failed = asked.Failed("NO_ROOM", "raw", Failed);

        Assert.Equal("NO_ROOM", failed.FailureReason);
        Assert.Equal("raw", failed.FailureDetail);
        Assert.Equal(Failed, failed.FailedAt);
        Assert.Null(failed.PendingId);
        Assert.Null(failed.AskedAt);
        Assert.Null(failed.LastSeenStatus);
    }

    [Fact]
    public void ReturnedToRest_ForgetsTheOperationAndTheStatusSeen_ButKeepsTheLastFailure()
    {
        VmOperation failed = VmOperation.None.Asked(Guid.NewGuid(), Asked, null).Failed("r", "d", Failed);
        VmOperation snapshotting = failed.Asked(null, null, VmStatus.Running);

        VmOperation rested = snapshotting.ReturnedToRest();

        Assert.Null(rested.LastSeenStatus);
        Assert.Null(rested.PendingId);
        Assert.Equal("r", rested.FailureReason);
        Assert.Equal("d", rested.FailureDetail);
        Assert.Equal(Failed, rested.FailedAt);
    }

    [Fact]
    public void EveryMove_ReturnsANewObject_AndLeavesTheOldOneAlone()
    {
        VmOperation none = VmOperation.None;

        VmOperation asked = none.Asked(Guid.NewGuid(), Asked, VmStatus.Running);

        Assert.Null(none.PendingId);
        Assert.NotEqual(none, asked);
    }

    [Fact]
    public void TwoObjectsWithTheSameFields_AreEqual()
    {
        Guid id = Guid.NewGuid();

        Assert.Equal(VmOperation.None.Asked(id, Asked, VmStatus.Running), VmOperation.None.Asked(id, Asked, VmStatus.Running));
    }

    [Fact]
    public void NoPropertyOfItIsPubliclyWritable()
    {
        foreach (var property in typeof(VmOperation).GetProperties())
        {
            Assert.False(property.SetMethod != null && property.SetMethod.IsPublic, property.Name);
        }
    }
}
