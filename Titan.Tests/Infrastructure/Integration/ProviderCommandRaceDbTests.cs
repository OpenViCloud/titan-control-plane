using Microsoft.EntityFrameworkCore;
using Titan.Application.Commands.Compute.Vm;
using Titan.Application.Commands.Compute.Vm.Contracts;
using Titan.Application.Services.Platform.ActivityEvent;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Aggregates.ProviderCommand;
using Titan.Domain.Aggregates.Tenant;
using Titan.Domain.Exceptions;
using Titan.Domain.Services;
using Titan.Domain.Values;
using Titan.Infrastructure.Endpoints;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.ProviderCommands;
using Titan.Infrastructure.Repositories;

namespace Titan.Tests.Infrastructure.Integration;

/// The race of two identical requests, on Postgres. Since REQUEST-END phase 1 a VM's keys carry the request id
/// (titan-ops architecture/resource-lifecycle.md#one-key-per-request), so the race of CC-01 — the second delete reading the
/// VM before the first committed — is settled by the row lock (PendingRequestsDbTests.TwoStops_AtOnce…), not by the key.
/// What is left to show here is the rollout seam: a VM whose delete row still carries the old key, from the image before
/// this one, takes a new delete as its own request, not as 409. Category=Integration — its own CI step; needs real
/// Postgres; no doubles (titan-ops architecture/testing.md#doubles-only-in-unit-tests).
[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public class ProviderCommandRaceDbTests(PostgresFixture pg)
{
    [Fact]
    public async Task ANewVmDelete_BesideAnOpenRowUnderTheOldKey_GetsItsOwnRow_AndIsNotRefused()
    {
        Tenant tenant = TestTenant.Active("race", $"kc-{Guid.NewGuid():N}", "race@titan.local");
        Vm vm = Vm.Create(tenant.Id, Guid.NewGuid(), $"vm-{Guid.NewGuid():N}"[..12], "m", "tpl", "cluster-1", 20);
        vm.MarkCreated($"vm{Guid.NewGuid():N}"[..28]);
        vm.MarkObserved(VmState.Running, "10.0.0.5");
        await using (TitanDbContext seed = pg.NewDbContext())
        {
            seed.Tenants.Add(tenant);
            seed.Vms.Add(vm);
            seed.ProviderCommands.Add(ProviderCommand.Create($"vm-delete-{vm.Id}", ProviderCommandKinds.VmDelete,
                "cluster-1", "{}", ProviderCommandRerun.AfterFailureOnly, DateTimeOffset.UtcNow));
            await seed.SaveChangesAsync();
        }

        await using (TitanDbContext db = pg.NewDbContext())
        {
            var caller = Callers.TenantMember(tenant.Id);
            var handler = new DeleteVmCommandHandler(new VmRepository(db), new TenantRepository(db), caller,
                new ProviderCommandQueue(new ProviderCommandRepository(db), TimeProvider.System),
                new VolumeRepository(db), new ActivityRecorder(new ActivityEventRepository(db), caller),
                new VmDeletion(new LoadBalancerRepository(db)));

            await handler.HandleAsync(new DeleteVmCommand(vm.Id));
        }

        await using TitanDbContext read = pg.NewDbContext();
        Vm stored = await read.Vms.SingleAsync(v => v.Id == vm.Id);
        Assert.Equal(VmRequestKind.Delete, stored.Pending!.Kind);
        Assert.Equal(1, await read.ActivityEvents.CountAsync(e => e.ResourceId == vm.Id && e.Action == "vm.delete_requested"));
        Assert.Equal(1, await read.ProviderCommands.CountAsync(c => c.Key == $"vm-delete-{vm.Id}-{stored.Pending.Id}"));
        Assert.Equal(2, await read.ProviderCommands.CountAsync(c => c.Key.StartsWith($"vm-delete-{vm.Id}")));
    }
}
