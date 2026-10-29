using Titan.Domain.Aggregates.Platform.ProviderCommand;
using Titan.Domain.Exceptions;

namespace Titan.Tests.Domain;

// titan-ops architecture/provider-and-worker.md#provider-command-table and #provider-command-key.
public class ProviderCommandTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 8, 0, 0, 0, TimeSpan.Zero);

    private static ProviderCommand NewCommand(ProviderCommandRerun rerun = ProviderCommandRerun.AfterAnyFinish) =>
        ProviderCommand.Create("vm-provision-x", ProviderCommandKinds.VmCreate, "mock", "{\"a\":1}", rerun, T0);

    private static ProviderCommand Leased(Guid leaseId, ProviderCommandRerun rerun = ProviderCommandRerun.AfterAnyFinish)
    {
        ProviderCommand command = NewCommand(rerun);
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

    [Fact]
    public void Reopen_ADoneRow_RunsAgain_UnderAfterAnyFinish()
    {
        Guid leaseId = Guid.NewGuid();
        ProviderCommand command = Leased(leaseId, ProviderCommandRerun.AfterAnyFinish);
        command.Complete(leaseId, T0);

        bool reopened = command.Reopen("{\"b\":2}", T0);

        Assert.True(reopened);
        Assert.Equal(ProviderCommandStatus.Pending, command.Status);
        Assert.Equal(0, command.Attempts);
        Assert.Equal("{\"b\":2}", command.Payload);
    }

    [Fact]
    public void Reopen_ADoneRow_IsAbsorbed_UnderAfterFailureOnly()
    {
        Guid leaseId = Guid.NewGuid();
        ProviderCommand command = Leased(leaseId, ProviderCommandRerun.AfterFailureOnly);
        command.Complete(leaseId, T0);

        Assert.False(command.Reopen("{}", T0));
        Assert.Equal(ProviderCommandStatus.Done, command.Status);
    }

    [Theory]
    [InlineData(ProviderCommandRerun.AfterAnyFinish)]
    [InlineData(ProviderCommandRerun.AfterFailureOnly)]
    public void Reopen_AFailedRow_RunsAgain(ProviderCommandRerun rerun)
    {
        Guid leaseId = Guid.NewGuid();
        ProviderCommand command = Leased(leaseId, rerun);
        command.Fail(leaseId, "gave up", retryable: false, T0);

        Assert.True(command.Reopen("{}", T0));
        Assert.Equal(ProviderCommandStatus.Pending, command.Status);
        Assert.Null(command.LastError);
    }
}
