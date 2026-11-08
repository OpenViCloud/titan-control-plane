using Titan.Domain.Aggregates.VmTemplate;
using Titan.Domain.Exceptions;

namespace Titan.Tests.Domain;

// VmTemplateOperation is what a template keeps about the sync it waits on in one cluster, the last one that gave up and
// whether the cluster was seen holding the alias while one waits (titan-ops features/catalog/design.md#template-state-is-existence).
// Every move returns an immutable object and leaves the old one as it was.
public class VmTemplateOperationTests
{
    private static readonly DateTimeOffset AskedAt = new(2026, 11, 8, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FailedAt = new(2026, 11, 8, 11, 0, 0, TimeSpan.Zero);

    [Fact]
    public void None_HoldsNothing()
    {
        VmTemplateOperation none = VmTemplateOperation.None;

        Assert.Null(none.PendingId);
        Assert.Null(none.AskedAt);
        Assert.Null(none.FailureReason);
        Assert.Null(none.FailureDetail);
        Assert.Null(none.FailedAt);
        Assert.Null(none.LastSeenStatus);
    }

    [Fact]
    public void Asked_HoldsTheSyncWhenItWasAskedAndTheStatusSeen()
    {
        Guid id = Guid.NewGuid();

        VmTemplateOperation asked = VmTemplateOperation.None.Asked(id, AskedAt, VmTemplateClusterStatus.Available);

        Assert.Equal(id, asked.PendingId);
        Assert.Equal(AskedAt, asked.AskedAt);
        Assert.Equal(VmTemplateClusterStatus.Available, asked.LastSeenStatus);
    }

    [Fact]
    public void Asked_KeepsTheLastFailure_SoItShowsUntilASyncEndsDone()
    {
        VmTemplateOperation failed = VmTemplateOperation.None.Asked(Guid.NewGuid(), AskedAt, null).Failed("NO_ROOM", "raw", FailedAt);

        VmTemplateOperation again = failed.Asked(Guid.NewGuid(), AskedAt, null);

        Assert.Equal("NO_ROOM", again.FailureReason);
        Assert.Equal("raw", again.FailureDetail);
        Assert.Equal(FailedAt, again.FailedAt);
    }

    [Fact]
    public void Asked_WithAnEmptyId_IsRefused_ForNothingCouldEndIt()
    {
        var thrown = Assert.Throws<BusinessException>(() => VmTemplateOperation.None.Asked(Guid.Empty, AskedAt, null));

        Assert.Equal("INVALID_REQUEST", thrown.Code);
    }

    [Fact]
    public void Saw_RecordsTheStatusSeen_AndKeepsTheSync()
    {
        Guid id = Guid.NewGuid();
        VmTemplateOperation asked = VmTemplateOperation.None.Asked(id, AskedAt, null);

        VmTemplateOperation seen = asked.Saw(VmTemplateClusterStatus.Available);

        Assert.Equal(VmTemplateClusterStatus.Available, seen.LastSeenStatus);
        Assert.Equal(id, seen.PendingId);
        Assert.Equal(AskedAt, seen.AskedAt);
        Assert.Null(seen.Saw(null).LastSeenStatus);
    }

    [Fact]
    public void Done_ForgetsTheSyncTheStatusSeenAndTheLastFailure()
    {
        VmTemplateOperation failed = VmTemplateOperation.None.Asked(Guid.NewGuid(), AskedAt, null).Failed("r", "d", FailedAt);
        VmTemplateOperation asked = failed.Asked(Guid.NewGuid(), AskedAt, VmTemplateClusterStatus.Available);

        Assert.Equal(VmTemplateOperation.None, asked.Done());
    }

    [Fact]
    public void Failed_RecordsTheFailure_AndNothingWaitsAnyMore_NorIsAStatusHeld()
    {
        VmTemplateOperation asked = VmTemplateOperation.None.Asked(Guid.NewGuid(), AskedAt, VmTemplateClusterStatus.Available);

        VmTemplateOperation failed = asked.Failed("NO_ROOM", "raw", FailedAt);

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
        VmTemplateOperation first = VmTemplateOperation.None.Asked(Guid.NewGuid(), AskedAt, null).Failed("OLD", "old", AskedAt);

        VmTemplateOperation second = first.Asked(Guid.NewGuid(), AskedAt, null).Failed("NEW", null, FailedAt);

        Assert.Equal("NEW", second.FailureReason);
        Assert.Null(second.FailureDetail);
        Assert.Equal(FailedAt, second.FailedAt);
    }

    [Fact]
    public void ReturnedToRest_ForgetsTheSyncAndTheStatusSeen_ButKeepsTheLastFailure()
    {
        VmTemplateOperation asked = VmTemplateOperation.None.Asked(Guid.NewGuid(), AskedAt, null).Failed("r", "d", FailedAt)
            .Asked(Guid.NewGuid(), AskedAt, VmTemplateClusterStatus.Available);

        VmTemplateOperation rested = asked.ReturnedToRest();

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
        VmTemplateOperation asked = VmTemplateOperation.None.Asked(id, AskedAt, VmTemplateClusterStatus.Available);

        asked.Failed("r", "d", FailedAt);
        asked.Done();
        asked.ReturnedToRest();
        asked.Saw(null);

        Assert.Equal(id, asked.PendingId);
        Assert.Equal(VmTemplateClusterStatus.Available, asked.LastSeenStatus);
        Assert.Null(asked.FailureReason);
    }

    [Fact]
    public void TwoObjectsWithTheSameFields_AreEqual()
    {
        Guid id = Guid.NewGuid();

        Assert.Equal(
            VmTemplateOperation.None.Asked(id, AskedAt, VmTemplateClusterStatus.Available),
            VmTemplateOperation.None.Asked(id, AskedAt, VmTemplateClusterStatus.Available));
    }

    [Fact]
    public void NoPropertyOfItIsPubliclyWritable()
    {
        foreach (var property in typeof(VmTemplateOperation).GetProperties())
        {
            Assert.False(property.SetMethod != null && property.SetMethod.IsPublic, property.Name);
        }
    }
}
