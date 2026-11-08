using Titan.Domain.Aggregates.Volume;
using Titan.Domain.Exceptions;

namespace Titan.Tests.Domain;

// VolumeSnapshotOperation is what a volume snapshot keeps about the operation it waits on, the last one that failed and whether Incus
// was seen holding the snapshot while one waits (titan-ops features/volume/design.md#volume-snapshot). Every move returns an
// immutable object and leaves the old one as it was.
public class VolumeSnapshotOperationTests
{
    private static readonly DateTimeOffset AskedAt = new(2026, 11, 7, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FailedAt = new(2026, 11, 7, 11, 0, 0, TimeSpan.Zero);

    [Fact]
    public void None_HoldsNothing()
    {
        VolumeSnapshotOperation none = VolumeSnapshotOperation.None;

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

        VolumeSnapshotOperation asked = VolumeSnapshotOperation.None.Asked(id, AskedAt, VolumeSnapshotStatus.Available);

        Assert.Equal(id, asked.PendingId);
        Assert.Equal(AskedAt, asked.AskedAt);
        Assert.Equal(VolumeSnapshotStatus.Available, asked.LastSeenStatus);
    }

    [Fact]
    public void Asked_KeepsTheLastFailure_SoItShowsUntilAnOperationEndsDone()
    {
        VolumeSnapshotOperation failed = VolumeSnapshotOperation.None.Asked(Guid.NewGuid(), AskedAt, null).Failed("NO_ROOM", "raw", FailedAt);

        VolumeSnapshotOperation again = failed.Asked(Guid.NewGuid(), AskedAt, null);

        Assert.Equal("NO_ROOM", again.FailureReason);
        Assert.Equal("raw", again.FailureDetail);
        Assert.Equal(FailedAt, again.FailedAt);
    }

    [Fact]
    public void Asked_WithAnEmptyId_IsRefused_ForNothingCouldEndIt()
    {
        var thrown = Assert.Throws<BusinessException>(() => VolumeSnapshotOperation.None.Asked(Guid.Empty, AskedAt, null));

        Assert.Equal("INVALID_REQUEST", thrown.Code);
    }

    [Fact]
    public void Saw_RecordsTheStatusSeen_AndKeepsTheOperation()
    {
        Guid id = Guid.NewGuid();
        VolumeSnapshotOperation asked = VolumeSnapshotOperation.None.Asked(id, AskedAt, null);

        VolumeSnapshotOperation seen = asked.Saw(VolumeSnapshotStatus.Available);

        Assert.Equal(VolumeSnapshotStatus.Available, seen.LastSeenStatus);
        Assert.Equal(id, seen.PendingId);
        Assert.Equal(AskedAt, seen.AskedAt);
        Assert.Null(seen.Saw(null).LastSeenStatus);
    }

    [Fact]
    public void Done_ForgetsTheOperationTheStatusSeenAndTheLastFailure()
    {
        VolumeSnapshotOperation failed = VolumeSnapshotOperation.None.Asked(Guid.NewGuid(), AskedAt, null).Failed("r", "d", FailedAt);
        VolumeSnapshotOperation asked = failed.Asked(Guid.NewGuid(), AskedAt, VolumeSnapshotStatus.Available);

        Assert.Equal(VolumeSnapshotOperation.None, asked.Done());
    }

    [Fact]
    public void Failed_RecordsTheFailure_AndNothingWaitsAnyMore_NorIsAStatusHeld()
    {
        VolumeSnapshotOperation asked = VolumeSnapshotOperation.None.Asked(Guid.NewGuid(), AskedAt, VolumeSnapshotStatus.Available);

        VolumeSnapshotOperation failed = asked.Failed("NO_ROOM", "raw", FailedAt);

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
        VolumeSnapshotOperation first = VolumeSnapshotOperation.None.Asked(Guid.NewGuid(), AskedAt, null).Failed("OLD", "old", AskedAt);

        VolumeSnapshotOperation second = first.Asked(Guid.NewGuid(), AskedAt, null).Failed("NEW", null, FailedAt);

        Assert.Equal("NEW", second.FailureReason);
        Assert.Null(second.FailureDetail);
        Assert.Equal(FailedAt, second.FailedAt);
    }

    [Fact]
    public void ReturnedToRest_ForgetsTheOperationAndTheStatusSeen_ButKeepsTheLastFailure()
    {
        VolumeSnapshotOperation asked = VolumeSnapshotOperation.None.Asked(Guid.NewGuid(), AskedAt, null).Failed("r", "d", FailedAt)
            .Asked(Guid.NewGuid(), AskedAt, VolumeSnapshotStatus.Available);

        VolumeSnapshotOperation rested = asked.ReturnedToRest();

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
        VolumeSnapshotOperation asked = VolumeSnapshotOperation.None.Asked(id, AskedAt, VolumeSnapshotStatus.Available);

        asked.Failed("r", "d", FailedAt);
        asked.Done();
        asked.ReturnedToRest();
        asked.Saw(null);

        Assert.Equal(id, asked.PendingId);
        Assert.Equal(VolumeSnapshotStatus.Available, asked.LastSeenStatus);
        Assert.Null(asked.FailureReason);
    }

    [Fact]
    public void TwoObjectsWithTheSameFields_AreEqual()
    {
        Guid id = Guid.NewGuid();

        Assert.Equal(
            VolumeSnapshotOperation.None.Asked(id, AskedAt, VolumeSnapshotStatus.Available),
            VolumeSnapshotOperation.None.Asked(id, AskedAt, VolumeSnapshotStatus.Available));
    }

    [Fact]
    public void NoPropertyOfItIsPubliclyWritable()
    {
        foreach (var property in typeof(VolumeSnapshotOperation).GetProperties())
        {
            Assert.False(property.SetMethod != null && property.SetMethod.IsPublic, property.Name);
        }
    }
}
