using Titan.Domain.Aggregates.FunctionRuntime;
using Titan.Domain.Exceptions;

namespace Titan.Tests.Domain;

// What a function runtime keeps about the sync it waits on in one cluster, the last one that gave up and what the
// cluster was last known to be while one waits (titan-ops features/ovc-function/design.md#runtimes). Every move returns
// an immutable object and leaves the old one as it was.
public class FunctionRuntimeOperationTests
{
    private static readonly DateTimeOffset AskedAt = new(2026, 11, 28, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FailedAt = new(2026, 11, 28, 11, 0, 0, TimeSpan.Zero);

    [Fact]
    public void None_HoldsNothing()
    {
        FunctionRuntimeOperation none = FunctionRuntimeOperation.None;

        Assert.Null(none.PendingId);
        Assert.Null(none.AskedAt);
        Assert.Null(none.FailureReason);
        Assert.Null(none.FailureDetail);
        Assert.Null(none.FailedAt);
        Assert.Null(none.LastSeenStatus);
    }

    [Fact]
    public void Asked_HoldsTheSync_AndKeepsTheLastFailure()
    {
        Guid id = Guid.NewGuid();
        FunctionRuntimeOperation failed = FunctionRuntimeOperation.None.Failed("why", "raw", FailedAt);

        FunctionRuntimeOperation asked = failed.Asked(id, AskedAt, FunctionRuntimeClusterStatus.NotFound);

        Assert.Equal((id, AskedAt, FunctionRuntimeClusterStatus.NotFound), (asked.PendingId!.Value, asked.AskedAt!.Value, asked.LastSeenStatus!.Value));
        Assert.Equal(("why", "raw", FailedAt), (asked.FailureReason, asked.FailureDetail, asked.FailedAt!.Value));
        Assert.Null(failed.PendingId);
    }

    [Fact]
    public void Asked_RefusesAnEmptyId()
    {
        var e = Assert.Throws<BusinessException>(() => FunctionRuntimeOperation.None.Asked(Guid.Empty, AskedAt, null));
        Assert.Equal("INVALID_REQUEST", e.Code);
    }

    [Fact]
    public void Saw_KeepsTheSync()
    {
        Guid id = Guid.NewGuid();
        FunctionRuntimeOperation asked = FunctionRuntimeOperation.None.Asked(id, AskedAt, null);

        FunctionRuntimeOperation seen = asked.Saw(FunctionRuntimeClusterStatus.Available);

        Assert.Equal((id, FunctionRuntimeClusterStatus.Available), (seen.PendingId!.Value, seen.LastSeenStatus!.Value));
        Assert.Null(asked.LastSeenStatus);
    }

    [Fact]
    public void Done_IsNone()
    {
        FunctionRuntimeOperation asked = FunctionRuntimeOperation.None.Failed("why", "raw", FailedAt).Asked(Guid.NewGuid(), AskedAt, null);

        Assert.Equal(FunctionRuntimeOperation.None, asked.Done());
    }

    [Fact]
    public void Failed_EndsTheWait_AndRecordsTheFailure()
    {
        FunctionRuntimeOperation asked = FunctionRuntimeOperation.None.Asked(Guid.NewGuid(), AskedAt, FunctionRuntimeClusterStatus.Available);

        FunctionRuntimeOperation failed = asked.Failed("why", "raw", FailedAt);

        Assert.Null(failed.PendingId);
        Assert.Null(failed.AskedAt);
        Assert.Null(failed.LastSeenStatus);
        Assert.Equal(("why", "raw", FailedAt), (failed.FailureReason, failed.FailureDetail, failed.FailedAt!.Value));
    }
}
