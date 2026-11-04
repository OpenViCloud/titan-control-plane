using Titan.Domain.Aggregates.ProviderCommand;
using Titan.Domain.Exceptions;

namespace Titan.Tests.Domain;

// titan-ops architecture/provider-and-worker.md#provider-command-table; a repeat of a key and the retention rule:
// titan-ops architecture/resource-lifecycle.md#one-key-per-request.
public class ProviderCommandTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 8, 0, 0, 0, TimeSpan.Zero);

    private static ProviderCommand NewCommand(DateTimeOffset? createdAt = null) =>
        ProviderCommand.Create("vm-provision-x", ProviderCommandKinds.VmCreate, "mock", "{\"a\":1}", createdAt ?? T0);

    private static ProviderCommand Leased(Guid leaseId, DateTimeOffset? createdAt = null)
    {
        ProviderCommand command = NewCommand(createdAt);
        command.Lease(leaseId, T0);
        return command;
    }

    [Fact]
    public void Create_IsPendingAndClaimableAtOnce()
    {
        ProviderCommand command = NewCommand();

        Assert.Equal(ProviderCommandStatus.Pending, command.Status);
        Assert.True(command.IsClaimable(T0));
        Assert.Equal(0, command.Attempts);
    }

    [Fact]
    public void Lease_TakesTheRow_ForTheLeaseLength_AndCountsTheAttempt()
    {
        Guid leaseId = Guid.NewGuid();
        ProviderCommand command = Leased(leaseId);

        Assert.Equal(ProviderCommandStatus.Leased, command.Status);
        Assert.Equal(leaseId, command.LeaseId);
        Assert.Equal(T0 + ProviderCommand.LeaseLength, command.LeaseUntil);
        Assert.Equal(1, command.Attempts);
        Assert.False(command.IsClaimable(T0 + TimeSpan.FromSeconds(59)));
    }

    [Fact]
    public void Lease_OfAHeldRow_IsRefused()
    {
        ProviderCommand command = Leased(Guid.NewGuid());

        ConflictException ex = Assert.Throws<ConflictException>(() => command.Lease(Guid.NewGuid(), T0));
        Assert.Equal("PROVIDER_COMMAND_NOT_CLAIMABLE", ex.Code);
    }

    [Fact]
    public void Lease_AfterTheLeaseRanOut_PassesToTheNewLease()
    {
        ProviderCommand command = Leased(Guid.NewGuid());
        Guid second = Guid.NewGuid();
        DateTimeOffset later = T0 + ProviderCommand.LeaseLength;

        command.Lease(second, later);

        Assert.Equal(second, command.LeaseId);
        Assert.Equal(2, command.Attempts);
    }

    [Fact]
    public void Complete_ByTheHolder_IsDone()
    {
        Guid leaseId = Guid.NewGuid();
        ProviderCommand command = Leased(leaseId);

        command.Complete(leaseId, T0);

        Assert.Equal(ProviderCommandStatus.Done, command.Status);
        Assert.Equal(T0, command.FinishedAt);
    }

    [Fact]
    public void Complete_ByAnotherLease_IsRefusedAsLeaseLost()
    {
        ProviderCommand command = Leased(Guid.NewGuid());

        ConflictException ex = Assert.Throws<ConflictException>(() => command.Complete(Guid.NewGuid(), T0));
        Assert.Equal("PROVIDER_COMMAND_LEASE_LOST", ex.Code);
    }

    [Fact]
    public void ExtendLease_ByAnotherLease_IsRefusedAsLeaseLost()
    {
        ProviderCommand command = Leased(Guid.NewGuid());

        ConflictException ex = Assert.Throws<ConflictException>(() => command.ExtendLease(Guid.NewGuid(), T0));
        Assert.Equal("PROVIDER_COMMAND_LEASE_LOST", ex.Code);
    }

    [Fact]
    public void Fail_Retryable_GoesBackToPending_AfterTheBackoff()
    {
        Guid leaseId = Guid.NewGuid();
        ProviderCommand command = Leased(leaseId);

        command.Fail(leaseId, "boom", retryable: true, T0);

        Assert.Equal(ProviderCommandStatus.Pending, command.Status);
        Assert.Equal(T0 + TimeSpan.FromSeconds(5), command.AvailableAt);
        Assert.False(command.IsClaimable(T0 + TimeSpan.FromSeconds(4)));
        Assert.True(command.IsClaimable(T0 + TimeSpan.FromSeconds(5)));
        Assert.Equal("boom", command.LastError);
    }

    [Fact]
    public void Fail_Retryable_OnTheLastAttempt_IsFailed()
    {
        ProviderCommand command = NewCommand();
        DateTimeOffset now = T0;
        for (int attempt = 1; attempt < ProviderCommand.MaxAttempts; attempt++)
        {
            Guid lease = Guid.NewGuid();
            command.Lease(lease, now);
            command.Fail(lease, "boom", retryable: true, now);
            now = command.AvailableAt;
        }
        Guid last = Guid.NewGuid();
        command.Lease(last, now);

        command.Fail(last, "boom", retryable: true, now);

        Assert.Equal(ProviderCommand.MaxAttempts, command.Attempts);
        Assert.Equal(ProviderCommandStatus.Failed, command.Status);
    }

    // A provider that refuses a few times and then does the work: the command ends Done on the lease that worked.
    [Fact]
    public void Fail_RetryableUntilTheLastLease_ThenComplete_IsDone()
    {
        ProviderCommand command = NewCommand();
        DateTimeOffset now = T0;
        for (int attempt = 1; attempt < ProviderCommand.MaxAttempts; attempt++)
        {
            Guid lease = Guid.NewGuid();
            command.Lease(lease, now);
            command.Fail(lease, "boom", retryable: true, now);
            Assert.Equal(ProviderCommandStatus.Pending, command.Status);
            now = command.AvailableAt;
        }
        Guid last = Guid.NewGuid();
        command.Lease(last, now);

        command.Complete(last, now);

        Assert.Equal(ProviderCommand.MaxAttempts, command.Attempts);
        Assert.Equal(ProviderCommandStatus.Done, command.Status);
    }

    // The last lease ran out without a report (the worker died): the next lease is beyond MaxAttempts, and any failure
    // it reports ends the row.
    [Fact]
    public void Fail_AfterTheLastLeaseRanOut_IsFailed()
    {
        ProviderCommand command = NewCommand();
        DateTimeOffset now = T0;
        for (int attempt = 1; attempt <= ProviderCommand.MaxAttempts; attempt++)
        {
            command.Lease(Guid.NewGuid(), now);
            now = now + ProviderCommand.LeaseLength;
        }
        Guid beyond = Guid.NewGuid();
        command.Lease(beyond, now);
        Assert.Equal(ProviderCommand.MaxAttempts + 1, command.Attempts);

        command.Fail(beyond, "gave up after the worker died", retryable: true, now);

        Assert.Equal(ProviderCommandStatus.Failed, command.Status);
    }

    [Fact]
    public void Fail_NotRetryable_IsFailedAtOnce()
    {
        Guid leaseId = Guid.NewGuid();
        ProviderCommand command = Leased(leaseId);

        command.Fail(leaseId, "no such image", retryable: false, T0);

        Assert.Equal(ProviderCommandStatus.Failed, command.Status);
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 10)]
    [InlineData(3, 20)]
    [InlineData(5, 80)]
    [InlineData(6, 120)]
    [InlineData(10, 120)]
    public void BackoffAfter_DoublesFromFiveSeconds_UpToTwoMinutes(int attempts, int seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), ProviderCommand.BackoffAfter(attempts));
    }

    [Fact]
    public void Reopen_AnOpenRow_IsAbsorbed()
    {
        ProviderCommand pending = NewCommand();
        ProviderCommand leased = Leased(Guid.NewGuid());

        Assert.False(pending.Reopen("{}", T0));
        Assert.False(leased.Reopen("{}", T0));
        Assert.Equal(ProviderCommandStatus.Leased, leased.Status);
    }

    // Under every key: what already happened does not happen twice. A resource's key carries its request id and only a
    // resent request meets it; a backup's key is the day's, asked every hour.
    [Fact]
    public void Reopen_ADoneRow_IsAbsorbed()
    {
        Guid leaseId = Guid.NewGuid();
        ProviderCommand command = Leased(leaseId);
        command.Complete(leaseId, T0);

        Assert.False(command.Reopen("{\"b\":2}", T0));
        Assert.Equal(ProviderCommandStatus.Done, command.Status);
        Assert.Equal("{\"a\":1}", command.Payload);
    }

    [Fact]
    public void Reopen_AFailedRow_RunsAgain_WithTheNewPayload()
    {
        Guid leaseId = Guid.NewGuid();
        ProviderCommand command = Leased(leaseId);
        command.Fail(leaseId, "gave up", retryable: false, T0);
        DateTimeOffset later = T0.AddMinutes(1);

        Assert.True(command.Reopen("{\"b\":2}", later));
        Assert.Equal(ProviderCommandStatus.Pending, command.Status);
        Assert.Equal(0, command.Attempts);
        Assert.Equal("{\"b\":2}", command.Payload);
        Assert.Equal(later, command.AvailableAt);
        Assert.Null(command.LastError);
        Assert.Null(command.FinishedAt);
    }

    // The retention rule: a finished row older than the cutoff goes; an open row stays however old, since a worker may
    // still lease it; a row of the cutoff instant or younger stays.
    [Theory]
    [InlineData(ProviderCommandStatus.Done, -31, true)]
    [InlineData(ProviderCommandStatus.Failed, -31, true)]
    [InlineData(ProviderCommandStatus.Pending, -31, false)]
    [InlineData(ProviderCommandStatus.Leased, -31, false)]
    [InlineData(ProviderCommandStatus.Done, -30, false)]
    [InlineData(ProviderCommandStatus.Failed, -29, false)]
    public void IsFinishedAndCreatedBefore_TakesOnlyFinishedRowsOlderThanTheCutoff(ProviderCommandStatus status, int ageDays,
        bool retired)
    {
        DateTimeOffset createdAt = T0.AddDays(ageDays);
        ProviderCommand command = NewCommand(createdAt);
        if (status != ProviderCommandStatus.Pending)
        {
            Guid leaseId = Guid.NewGuid();
            command.Lease(leaseId, createdAt);
            if (status == ProviderCommandStatus.Done)
            {
                command.Complete(leaseId, createdAt);
            }
            if (status == ProviderCommandStatus.Failed)
            {
                command.Fail(leaseId, "gave up", retryable: false, createdAt);
            }
        }

        bool taken = command.IsFinishedAndCreatedBefore(T0.AddDays(-30));

        Assert.Equal(status, command.Status);
        Assert.Equal(retired, taken);
    }
}
