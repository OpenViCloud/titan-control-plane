using Microsoft.EntityFrameworkCore;
using Titan.Domain.Aggregates.Vm;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.Repositories;

namespace Titan.Tests.Infrastructure;

// The key addition read side (titan-ops architecture/code-structure.md#child-entity): a list per VM, and one by its
// own id. SshKeyIds is a Guid array column, mapped through the backing field — this pins that the InMemory provider
// EF Core runs these tests on reads it back, in the order it was written.
public class VmKeyAdditionRepositoryStateTests
{
    private static readonly Guid TenantId = Guid.NewGuid();

    private static TitanDbContext NewDb() =>
        new(new DbContextOptionsBuilder<TitanDbContext>()
            .UseInMemoryDatabase($"vm-key-addition-state-{Guid.NewGuid()}")
            .Options);

    private static Vm RunningVm()
    {
        var vm = Vm.Create(TenantId, Guid.NewGuid(), "vm-1", "m", "tpl-1", "cluster-a", 20);
        vm.MarkRunning("42", "10.0.0.5");
        return vm;
    }

    [Fact]
    public async Task FindByVmIdAsync_ReadsBackTheSshKeyIds_NewestFirst()
    {
        Vm vm = RunningVm();
        List<Guid> first = [Guid.NewGuid(), Guid.NewGuid()];
        List<Guid> second = [Guid.NewGuid()];
        Guid firstId = Guid.NewGuid();
        Guid firstOp = Guid.NewGuid();
        vm.AddKeys(firstId, firstOp, first);
        vm.KeyAdditionSucceeded(firstId, firstOp, false); // only one may be Adding at a time
        vm.AddKeys(Guid.NewGuid(), Guid.NewGuid(), second);
        VmKeyAddition secondAddition = vm.KeyAdditions.Last();

        await using TitanDbContext db = NewDb();
        db.Vms.Add(vm);
        await db.SaveChangesAsync();

        var repo = new VmKeyAdditionRepository(db);
        List<VmKeyAddition> found = await repo.FindByVmIdAsync(vm.Id);

        Assert.Equal(2, found.Count);
        Assert.Equal(secondAddition.Id, found[0].Id);
        Assert.Equal(second, found[0].SshKeyIds);
        Assert.Equal(first, found[1].SshKeyIds);
    }

    [Fact]
    public async Task FindByIdAsync_ReadsOneAddition_WhateverItsStatus()
    {
        Vm vm = RunningVm();
        Guid operationId = Guid.NewGuid();
        List<Guid> ids = [Guid.NewGuid()];
        vm.AddKeys(Guid.NewGuid(), operationId, ids);
        VmKeyAddition addition = vm.KeyAdditions.Single();
        vm.KeyAdditionFailed(addition.Id, operationId, "AGENT_NOT_READY", "detail");

        await using TitanDbContext db = NewDb();
        db.Vms.Add(vm);
        await db.SaveChangesAsync();

        var repo = new VmKeyAdditionRepository(db);
        VmKeyAddition? found = await repo.FindByIdAsync(addition.Id);

        Assert.NotNull(found);
        Assert.Equal(VmKeyAdditionStatus.Failed, found!.Status);
        Assert.Equal(ids, found.SshKeyIds);
        Assert.Equal("AGENT_NOT_READY", found.FailureReason);
    }

    [Fact]
    public async Task FindByIdAsync_UnknownId_IsNull()
    {
        await using TitanDbContext db = NewDb();
        var repo = new VmKeyAdditionRepository(db);

        Assert.Null(await repo.FindByIdAsync(Guid.NewGuid()));
    }
}
