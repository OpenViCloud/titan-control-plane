using Titan.Application.Commands.Platform.ProviderCommand;
using Titan.Application.Commands.Platform.ProviderCommand.Contracts;
using Titan.Application.Queries.Platform.ProviderCommand;
using Titan.Application.Queries.Platform.ProviderCommand.Contracts;
using Titan.Domain.Aggregates.ProviderCommand;
using Titan.Domain.Exceptions;
using Titan.Tests.Fakes;

namespace Titan.Tests.Services;

// titan-ops architecture/provider-and-worker.md#provider-command-lease: PUT a lease, GET what it holds, report on it.
public class ProviderCommandLeaseTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 8, 0, 0, 0, TimeSpan.Zero);
    private readonly FakeProviderCommandRepository _repo = new();
    private readonly FixedClock _clock = new(T0);

    private ProviderCommand Seed(string key, string kind, DateTimeOffset createdAt)
    {
        ProviderCommand command = ProviderCommand.Create(key, kind, "mock", "{\"vmId\":\"x\"}",
            ProviderCommandRerun.AfterAnyFinish, createdAt);
        _repo.Add(command);
        return command;
    }

    private Task LeaseAsync(Guid leaseId, params string[] kinds) =>
        new LeaseProviderCommandCommandHandler(_repo, _clock).HandleAsync(new LeaseProviderCommandCommand(leaseId, kinds));

    private Task<LeasedProviderCommandResult> GetAsync(Guid leaseId) =>
        new GetLeasedProviderCommandQueryHandler(_repo).HandleAsync(new GetLeasedProviderCommandQuery(leaseId));

    [Fact]
    public async Task Lease_TakesTheOldestClaimableRowOfItsKinds()
    {
        Seed("network-create-1", ProviderCommandKinds.NetworkCreate, T0.AddMinutes(-10));
        ProviderCommand older = Seed("vm-provision-1", ProviderCommandKinds.VmCreate, T0.AddMinutes(-5));
        Seed("vm-provision-2", ProviderCommandKinds.VmCreate, T0.AddMinutes(-1));
        Guid leaseId = Guid.NewGuid();

        await LeaseAsync(leaseId, ProviderCommandKinds.VmCreate);

        LeasedProviderCommandResult leased = await GetAsync(leaseId);
        Assert.Equal(older.Id, leased.Id);
        Assert.Equal("vm-provision-1", leased.Key);
        Assert.Equal("x", leased.Payload.GetProperty("vmId").GetString());
        Assert.Equal(1, leased.Attempt);
        Assert.Equal(ProviderCommand.MaxAttempts, leased.MaxAttempts);
    }

    [Fact]
    public async Task Lease_WithNothingDue_LeavesTheLeaseEmpty_AndGetAnswers404()
    {
        Seed("volume-create-1", ProviderCommandKinds.VolumeCreate, T0);
        Guid leaseId = Guid.NewGuid();

        await LeaseAsync(leaseId, ProviderCommandKinds.VmCreate);

        NotFoundException ex = await Assert.ThrowsAsync<NotFoundException>(() => GetAsync(leaseId));
        Assert.Equal("PROVIDER_COMMAND_LEASE_NOT_FOUND", ex.Code);
    }

    [Fact]
    public async Task Lease_RepeatedWithTheSameLeaseId_IsANoOp()
    {
        ProviderCommand first = Seed("vm-provision-1", ProviderCommandKinds.VmCreate, T0.AddMinutes(-2));
        ProviderCommand second = Seed("vm-provision-2", ProviderCommandKinds.VmCreate, T0.AddMinutes(-1));
        Guid leaseId = Guid.NewGuid();

        await LeaseAsync(leaseId, ProviderCommandKinds.VmCreate);
        await LeaseAsync(leaseId, ProviderCommandKinds.VmCreate);

        Assert.Equal(ProviderCommandStatus.Leased, first.Status);
        Assert.Equal(ProviderCommandStatus.Pending, second.Status);
        Assert.Equal(1, first.Attempts);
    }

    [Fact]
    public async Task Complete_RepeatedAfterItLanded_IsANoOp()
    {
        ProviderCommand command = Seed("vm-provision-1", ProviderCommandKinds.VmCreate, T0);
        Guid leaseId = Guid.NewGuid();
        await LeaseAsync(leaseId, ProviderCommandKinds.VmCreate);
        var handler = new CompleteProviderCommandCommandHandler(_repo, _clock);

        await handler.HandleAsync(new CompleteProviderCommandCommand(leaseId));
        await handler.HandleAsync(new CompleteProviderCommandCommand(leaseId));

        Assert.Equal(ProviderCommandStatus.Done, command.Status);
        Assert.Equal(2, _repo.Saves);
    }

    [Fact]
    public async Task Fail_RepeatedAfterItLanded_IsANoOp_AndDoesNotSpendAnAttempt()
    {
        ProviderCommand command = Seed("vm-provision-1", ProviderCommandKinds.VmCreate, T0);
        Guid leaseId = Guid.NewGuid();
        await LeaseAsync(leaseId, ProviderCommandKinds.VmCreate);
        var handler = new FailProviderCommandCommandHandler(_repo, _clock);

        await handler.HandleAsync(new FailProviderCommandCommand(leaseId, "timeout", Retryable: true));
        await handler.HandleAsync(new FailProviderCommandCommand(leaseId, "timeout", Retryable: true));

        Assert.Equal(ProviderCommandStatus.Pending, command.Status);
        Assert.Equal(1, command.Attempts);
    }

    [Fact]
    public async Task Report_ByALeaseThatHoldsNothing_Is404()
    {
        var handler = new CompleteProviderCommandCommandHandler(_repo, _clock);

        NotFoundException ex = await Assert.ThrowsAsync<NotFoundException>(
            () => handler.HandleAsync(new CompleteProviderCommandCommand(Guid.NewGuid())));
        Assert.Equal("PROVIDER_COMMAND_LEASE_NOT_FOUND", ex.Code);
    }

    // The first lease ran out and a second worker took the row: the first worker's late report finds nothing under
    // its lease id.
    [Fact]
    public async Task Report_ByALeaseThatWasTakenOver_Is404()
    {
        Seed("vm-provision-1", ProviderCommandKinds.VmCreate, T0);
        Guid first = Guid.NewGuid();
        await LeaseAsync(first, ProviderCommandKinds.VmCreate);
        _clock.Now = T0 + ProviderCommand.LeaseLength;
        await LeaseAsync(Guid.NewGuid(), ProviderCommandKinds.VmCreate);

        NotFoundException ex = await Assert.ThrowsAsync<NotFoundException>(
            () => new CompleteProviderCommandCommandHandler(_repo, _clock).HandleAsync(new CompleteProviderCommandCommand(first)));
        Assert.Equal("PROVIDER_COMMAND_LEASE_NOT_FOUND", ex.Code);
    }

    [Fact]
    public async Task Extend_ByTheHolder_MovesTheLeaseEnd()
    {
        ProviderCommand command = Seed("vm-provision-1", ProviderCommandKinds.VmCreate, T0);
        Guid leaseId = Guid.NewGuid();
        await LeaseAsync(leaseId, ProviderCommandKinds.VmCreate);
        _clock.Now = T0.AddSeconds(30);

        await new ExtendProviderCommandLeaseCommandHandler(_repo, _clock)
            .HandleAsync(new ExtendProviderCommandLeaseCommand(leaseId));

        Assert.Equal(T0.AddSeconds(30) + ProviderCommand.LeaseLength, command.LeaseUntil);
    }
}
