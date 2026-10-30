using Microsoft.EntityFrameworkCore;
using Titan.Application.Commands.Compute.Vm;
using Titan.Application.Commands.Compute.Vm.Contracts;
using Titan.Application.Services.Platform.ActivityEvent;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Aggregates.ProviderCommand;
using Titan.Domain.Aggregates.Tenant;
using Titan.Domain.Exceptions;
using Titan.Domain.Values;
using Titan.Infrastructure.Endpoints;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.ProviderCommands;
using Titan.Infrastructure.Repositories;

namespace Titan.Tests.Infrastructure.Integration;

/// Two identical requests at once where the first commits before the second reads the command key
/// (titan-ops architecture/provider-and-worker.md#provider-command-key). The second read the resource before that commit, so
/// it took the request as new; the open row it then finds is the other request's work. Category=Integration — its own
/// CI step; needs real Postgres; no doubles (titan-ops architecture/testing.md#doubles-only-in-unit-tests).
[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public class ProviderCommandRaceDbTests(PostgresFixture pg)
{
    // CC-01 of https://github.com/OpenViCloud/titan-regression/blob/main/plan.md: the second of two deletes answered 202 and recorded a second vm.delete_requested.
    [Fact]
    public async Task ANewDelete_FindingTheOpenCommandOfAnotherRequest_Is409_AndRecordsNothing()
    {
        Tenant tenant = TestTenant.Active("race", $"kc-{Guid.NewGuid():N}", "race@titan.local");
        Vm vm = Vm.CreateUnchecked(tenant.Id, Guid.NewGuid(), $"vm-{Guid.NewGuid():N}"[..12], "m", "tpl", "cluster-1", 20);
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
                new VolumeRepository(db), new LoadBalancerRepository(db),
                new ActivityRecorder(new ActivityEventRepository(db), caller));

            ConflictException refused = await Assert.ThrowsAsync<ConflictException>(
                () => handler.HandleAsync(new DeleteVmCommand(vm.Id)));

            Assert.Equal(ErrorStatus.RequestInProgressCode, refused.Code);
        }

        await using TitanDbContext read = pg.NewDbContext();
        Assert.Equal(0, await read.ActivityEvents.CountAsync(e => e.ResourceId == vm.Id));
        Vm stored = await read.Vms.SingleAsync(v => v.Id == vm.Id);
        Assert.Equal(DesiredExistence.Exists, stored.DesiredExistence);
    }
}
