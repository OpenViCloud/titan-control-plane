using Titan.Domain.Aggregates.Network;
using Titan.Domain.Exceptions;

namespace Titan.Tests.Domain;

// NetworkOperation is what a network keeps about the operation it waits on and the last one that failed (titan-ops
// features/network/design.md#network-operation). Every move returns an immutable object and leaves the old one as it was.
public class NetworkOperationTests
{
    private static readonly DateTimeOffset AskedAt = new(2026, 11, 7, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset FailedAt = new(2026, 11, 7, 11, 0, 0, TimeSpan.Zero);

    [Fact]
    public void None_HoldsNothing()
    {
        NetworkOperation none = NetworkOperation.None;

        Assert.Null(none.PendingId);
        Assert.Null(none.Kind);
        Assert.Null(none.AskedAt);
        Assert.Null(none.FailureReason);
        Assert.Null(none.FailureDetail);
        Assert.Null(none.FailedAt);
    }

    [Fact]
    public void Asked_HoldsTheOperationItsKindAndWhenItWasAsked()
    {
        Guid id = Guid.NewGuid();

        NetworkOperation asked = NetworkOperation.None.Asked(id, NetworkOperationKind.Delete, AskedAt);

        Assert.Equal(id, asked.PendingId);
        Assert.Equal(NetworkOperationKind.Delete, asked.Kind);
        Assert.Equal(AskedAt, asked.AskedAt);
    }

    [Fact]
    public void Asked_KeepsTheLastFailure_SoItShowsUntilAnOperationEndsDone()
    {
        NetworkOperation failed = NetworkOperation.None.Asked(Guid.NewGuid(), NetworkOperationKind.Create, AskedAt).Failed("NO_ROOM", "raw", FailedAt);

        NetworkOperation again = failed.Asked(Guid.NewGuid(), NetworkOperationKind.Delete, AskedAt);

        Assert.Equal("NO_ROOM", again.FailureReason);
        Assert.Equal("raw", again.FailureDetail);
        Assert.Equal(FailedAt, again.FailedAt);
    }

    [Fact]
    public void Asked_WithAnEmptyId_IsRefused_ForNothingCouldEndIt()
    {
        var thrown = Assert.Throws<BusinessException>(() => NetworkOperation.None.Asked(Guid.Empty, NetworkOperationKind.Create, AskedAt));

        Assert.Equal("INVALID_REQUEST", thrown.Code);
    }

    [Fact]
    public void Done_ForgetsTheOperationAndTheLastFailure()
    {
        NetworkOperation failed = NetworkOperation.None.Asked(Guid.NewGuid(), NetworkOperationKind.Create, AskedAt).Failed("r", "d", FailedAt);
        NetworkOperation asked = failed.Asked(Guid.NewGuid(), NetworkOperationKind.Delete, AskedAt);

        Assert.Equal(NetworkOperation.None, asked.Done());
    }

    [Fact]
    public void Failed_RecordsTheFailure_AndNothingWaitsAnyMore()
    {
        NetworkOperation asked = NetworkOperation.None.Asked(Guid.NewGuid(), NetworkOperationKind.Delete, AskedAt);

        NetworkOperation failed = asked.Failed("NO_ROOM", "raw", FailedAt);

        Assert.Null(failed.PendingId);
        Assert.Null(failed.Kind);
        Assert.Null(failed.AskedAt);
        Assert.Equal("NO_ROOM", failed.FailureReason);
        Assert.Equal("raw", failed.FailureDetail);
        Assert.Equal(FailedAt, failed.FailedAt);
    }

    [Fact]
    public void Failed_ReplacesAnOlderFailure()
    {
        NetworkOperation first = NetworkOperation.None.Asked(Guid.NewGuid(), NetworkOperationKind.Create, AskedAt).Failed("OLD", "old", AskedAt);

        NetworkOperation second = first.Asked(Guid.NewGuid(), NetworkOperationKind.Delete, AskedAt).Failed("NEW", null, FailedAt);

        Assert.Equal("NEW", second.FailureReason);
        Assert.Null(second.FailureDetail);
        Assert.Equal(FailedAt, second.FailedAt);
    }

    [Fact]
    public void ReturnedToRest_ForgetsTheOperation_ButKeepsTheLastFailure()
    {
        NetworkOperation asked = NetworkOperation.None.Asked(Guid.NewGuid(), NetworkOperationKind.Create, AskedAt).Failed("r", "d", FailedAt)
            .Asked(Guid.NewGuid(), NetworkOperationKind.Delete, AskedAt);

        NetworkOperation rested = asked.ReturnedToRest();

        Assert.Null(rested.PendingId);
        Assert.Null(rested.Kind);
        Assert.Null(rested.AskedAt);
        Assert.Equal("r", rested.FailureReason);
        Assert.Equal("d", rested.FailureDetail);
        Assert.Equal(FailedAt, rested.FailedAt);
    }

    [Fact]
    public void EveryMove_LeavesTheOldObjectAsItWas()
    {
        Guid id = Guid.NewGuid();
        NetworkOperation asked = NetworkOperation.None.Asked(id, NetworkOperationKind.Delete, AskedAt);

        asked.Failed("r", "d", FailedAt);
        asked.Done();
        asked.ReturnedToRest();

        Assert.Equal(id, asked.PendingId);
        Assert.Equal(NetworkOperationKind.Delete, asked.Kind);
        Assert.Null(asked.FailureReason);
    }
}
