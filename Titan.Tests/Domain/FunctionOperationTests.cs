using Titan.Domain.Aggregates.Function;
using Titan.Domain.Exceptions;

namespace Titan.Tests.Domain;

// FunctionOperation is immutable: every move returns a new value and leaves the old one as it was.
public class FunctionOperationTests
{
    [Fact]
    public void Asked_WaitsOnTheId_AndRemembersWhatWasSeen_KeepingTheLastFailure()
    {
        Guid id = Guid.NewGuid();
        FunctionOperation failed = FunctionOperation.None.Failed("reason", "detail", DateTimeOffset.UnixEpoch);

        FunctionOperation asked = failed.Asked(id, DateTimeOffset.UnixEpoch, FunctionStatus.Active);

        Assert.Equal(id, asked.PendingId);
        Assert.Equal(FunctionStatus.Active, asked.LastSeenStatus);
        Assert.Equal("reason", asked.FailureReason);
        Assert.Null(failed.PendingId);
    }

    [Fact]
    public void Asked_WithAnEmptyId_IsRefused()
    {
        Assert.Throws<BusinessException>(() => FunctionOperation.None.Asked(Guid.Empty, DateTimeOffset.UnixEpoch, null));
    }

    [Fact]
    public void Done_ClearsEverything_AndFailed_RecordsTheFailure_AndNothingWaits()
    {
        FunctionOperation asked = FunctionOperation.None.Asked(Guid.NewGuid(), DateTimeOffset.UnixEpoch, FunctionStatus.Active);

        Assert.Equal(FunctionOperation.None, asked.Done());
        FunctionOperation failed = asked.Failed("reason", "detail", DateTimeOffset.UnixEpoch);
        Assert.Null(failed.PendingId);
        Assert.Null(failed.AskedAt);
        Assert.Null(failed.LastSeenStatus);
        Assert.Equal("detail", failed.FailureDetail);
        Assert.Equal(DateTimeOffset.UnixEpoch, failed.FailedAt);
    }
}
