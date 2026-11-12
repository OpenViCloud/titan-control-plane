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
        ProviderCommand command = ProviderCommand.Create(key, kind, "mock", "{\"vmId\":\"x\"}", createdAt);
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
        var handler = new CompleteProviderCommandCommandHandler(_repo);

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
        var handler = new CompleteProviderCommandCommandHandler(_repo);

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
            () => new CompleteProviderCommandCommandHandler(_repo).HandleAsync(new CompleteProviderCommandCommand(first)));
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

    // titan-ops features/provider/design.md#tenant-lane: one command of a tenant at a time in each lane, for every cluster;
    // other tenants and the no-tenant lane do not wait for it.
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    private ProviderCommand SeedFor(Guid? tenantId, string key, string kind, DateTimeOffset createdAt,
        string clusterId = "mock")
    {
        ProviderCommand command = ProviderCommand.Create(key, kind, clusterId, "{}", createdAt, tenantId);
        _repo.Add(command);
        return command;
    }

    private static readonly string[] TenantKinds =
        [ProviderCommandKinds.VmCreate, ProviderCommandKinds.VmDelete, ProviderCommandKinds.TemplateSync];

    [Fact]
    public async Task Lease_SkipsATenantThatHoldsALease_AndGivesTheNextTenant()
    {
        SeedFor(TenantA, "a-1", ProviderCommandKinds.VmCreate, T0.AddMinutes(-3));
        ProviderCommand a2 = SeedFor(TenantA, "a-2", ProviderCommandKinds.VmCreate, T0.AddMinutes(-2));
        ProviderCommand b1 = SeedFor(TenantB, "b-1", ProviderCommandKinds.VmCreate, T0.AddMinutes(-1));

        await LeaseAsync(Guid.NewGuid(), TenantKinds);
        await LeaseAsync(Guid.NewGuid(), TenantKinds);

        Assert.Equal(ProviderCommandStatus.Pending, a2.Status);
        Assert.Equal(ProviderCommandStatus.Leased, b1.Status);
    }

    // titan-ops features/provider/user-stories.md#p3-2, the two other ways a command stops running: it failed for good,
    // or failed and waits for its retry.
    [Theory]
    [InlineData(false, ProviderCommandStatus.Failed)]
    [InlineData(true, ProviderCommandStatus.Pending)]
    public async Task Lease_GivesATenantsNextCommand_OnceItsLeasedOneFailed(bool retryable, ProviderCommandStatus failedTo)
    {
        ProviderCommand a1 = SeedFor(TenantA, "a-1", ProviderCommandKinds.VmCreate, T0.AddMinutes(-2));
        ProviderCommand a2 = SeedFor(TenantA, "a-2", ProviderCommandKinds.VmDelete, T0.AddMinutes(-1));
        Guid first = Guid.NewGuid();
        await LeaseAsync(first, TenantKinds);

        await new FailProviderCommandCommandHandler(_repo, _clock)
            .HandleAsync(new FailProviderCommandCommand(first, "boom", retryable));
        await LeaseAsync(Guid.NewGuid(), TenantKinds);

        Assert.Equal(failedTo, a1.Status);
        Assert.Equal(ProviderCommandStatus.Leased, a2.Status);
    }

    [Fact]
    public async Task Lease_GivesATenantsNextCommand_OnceItsLeasedOneStoppedRunning()
    {
        ProviderCommand a1 = SeedFor(TenantA, "a-1", ProviderCommandKinds.VmCreate, T0.AddMinutes(-2));
        ProviderCommand a2 = SeedFor(TenantA, "a-2", ProviderCommandKinds.VmDelete, T0.AddMinutes(-1));
        Guid first = Guid.NewGuid();
        await LeaseAsync(first, TenantKinds);

        await new CompleteProviderCommandCommandHandler(_repo).HandleAsync(new CompleteProviderCommandCommand(first));
        await LeaseAsync(Guid.NewGuid(), TenantKinds);

        Assert.Equal(ProviderCommandStatus.Done, a1.Status);
        Assert.Equal(ProviderCommandStatus.Leased, a2.Status);
    }

    [Fact]
    public async Task Lease_KeepsATenantsCommandsInOneLane_AcrossClusters()
    {
        SeedFor(TenantA, "a-x", ProviderCommandKinds.VmCreate, T0.AddMinutes(-2), clusterId: "cluster-x");
        ProviderCommand ay = SeedFor(TenantA, "a-y", ProviderCommandKinds.VmCreate, T0.AddMinutes(-1), clusterId: "cluster-y");

        await LeaseAsync(Guid.NewGuid(), TenantKinds);
        await LeaseAsync(Guid.NewGuid(), TenantKinds);

        Assert.Equal(ProviderCommandStatus.Pending, ay.Status);
    }

    [Fact]
    public async Task Lease_WhoseLeaseRanOut_IsTakenAgain_BeforeTheTenantsOtherCommands()
    {
        ProviderCommand a1 = SeedFor(TenantA, "a-1", ProviderCommandKinds.VmCreate, T0.AddMinutes(-2));
        ProviderCommand a2 = SeedFor(TenantA, "a-2", ProviderCommandKinds.VmCreate, T0.AddMinutes(-1));
        await LeaseAsync(Guid.NewGuid(), TenantKinds);
        _clock.Now = T0 + ProviderCommand.LeaseLength + TimeSpan.FromSeconds(1);
        Guid second = Guid.NewGuid();

        await LeaseAsync(second, TenantKinds);

        Assert.Equal(second, a1.LeaseId);
        Assert.Equal(2, a1.Attempts);
        Assert.Equal(ProviderCommandStatus.Pending, a2.Status);
    }

    [Fact]
    public async Task Lease_OfTheBackupKinds_IsALaneOfItsOwn_ForTheSameTenant()
    {
        SeedFor(TenantA, "a-create", ProviderCommandKinds.VmCreate, T0.AddMinutes(-2));
        ProviderCommand backup = SeedFor(TenantA, "a-backup", ProviderCommandKinds.VmBackup, T0.AddMinutes(-1));
        await LeaseAsync(Guid.NewGuid(), TenantKinds);

        await LeaseAsync(Guid.NewGuid(), ProviderCommandKinds.VmBackup, ProviderCommandKinds.VolumeBackup);

        Assert.Equal(ProviderCommandStatus.Leased, backup.Status);
    }

    [Fact]
    public async Task TemplateSync_HasNoTenant_SoItNeitherWaitsForNorBlocksATenant()
    {
        SeedFor(TenantA, "a-1", ProviderCommandKinds.VmCreate, T0.AddMinutes(-3));
        ProviderCommand sync = SeedFor(null, "template-sync-1", ProviderCommandKinds.TemplateSync, T0.AddMinutes(-2));
        ProviderCommand b1 = SeedFor(TenantB, "b-1", ProviderCommandKinds.VmCreate, T0.AddMinutes(-1));

        await LeaseAsync(Guid.NewGuid(), TenantKinds);
        await LeaseAsync(Guid.NewGuid(), TenantKinds);
        await LeaseAsync(Guid.NewGuid(), TenantKinds);

        Assert.Equal(ProviderCommandStatus.Leased, sync.Status);
        Assert.Equal(ProviderCommandStatus.Leased, b1.Status);
    }

    [Fact]
    public async Task TemplateSyncs_RunOneAtATime()
    {
        SeedFor(null, "template-sync-1", ProviderCommandKinds.TemplateSync, T0.AddMinutes(-2));
        ProviderCommand second = SeedFor(null, "template-sync-2", ProviderCommandKinds.TemplateSync, T0.AddMinutes(-1));

        await LeaseAsync(Guid.NewGuid(), TenantKinds);
        await LeaseAsync(Guid.NewGuid(), TenantKinds);

        Assert.Equal(ProviderCommandStatus.Pending, second.Status);
    }
}
