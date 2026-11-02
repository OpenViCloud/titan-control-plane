using Microsoft.EntityFrameworkCore;
using Titan.Application.Commands.Compute.LoadBalancer;
using Titan.Application.Commands.Compute.LoadBalancer.Contracts;
using Titan.Application.Commands.Compute.Vm;
using Titan.Application.Commands.Compute.Vm.Contracts;
using Titan.Application.Services.Platform.ActivityEvent;
using Titan.Domain.Aggregates.Network;
using Titan.Domain.Aggregates.Tenant;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Exceptions;
using Titan.Domain.Services;
using Titan.Domain.Values;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.ProviderCommands;
using Titan.Infrastructure.Repositories;

namespace Titan.Tests.Infrastructure.Integration;

/// A VM a load balancer still sends to is not deleted (titan-ops architecture/domain-services.md#cross-aggregate-lock): the
/// delete and a load balancer create that names the VM as a backend take turns on the VM's row, so the second sees the
/// first. Without the lock both can commit, and the load balancer sends to a VM being deleted; the two requests are only
/// started together, not forced to interleave, so a run without the lock may also pass. Category=Integration — needs
/// real Postgres, since the lock is a Postgres row lock (titan-ops architecture/testing.md#doubles-only-in-unit-tests).
[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public class VmDeletionRaceDbTests(PostgresFixture pg)
{
    private const string Cluster = "cluster-1";
    private const string Subnet = "2001:41d0:d00:d405::/64";

    [Fact]
    public async Task DeleteAndALoadBalancerCreateNamingTheVm_OneIsRefused_AndNoLoadBalancerSendsToADeletedVm()
    {
        Tenant tenant = TestTenant.Active($"vd-race-{Guid.NewGuid():N}"[..20], $"kc-{Guid.NewGuid():N}", "vd-race@titan.local");
        Network network = Network.Create(tenant.Id, NetworkName.Parse($"net-{Guid.NewGuid():N}"[..12]), Cluster, null, wantsPublicIpv6: true);
        network.ReceivePublicIpv6(Subnet);
        network.MarkActive($"net{Guid.NewGuid():N}"[..20], "10.0.0.0/24", "10.0.0.1");
        Vm vm = Vm.Create(tenant.Id, network.Id, $"vm-{Guid.NewGuid():N}"[..12], "m", "tpl", Cluster, 20);
        vm.MarkCreated($"vm{Guid.NewGuid():N}"[..28]);
        vm.MarkObserved(VmState.Running, "10.0.0.5", null);
        await using (TitanDbContext seed = pg.NewDbContext())
        {
            seed.Tenants.Add(tenant);
            seed.Networks.Add(network);
            seed.Vms.Add(vm);
            await seed.SaveChangesAsync();
        }

        // Two requests, two contexts, started together: without the lock each reads the other's side before it commits.
        await using TitanDbContext deleting = pg.NewDbContext();
        await using TitanDbContext creating = pg.NewDbContext();
        Task<Exception?> delete = TryAsync(() => DeleteVmHandler(deleting, tenant.Id).HandleAsync(new DeleteVmCommand(vm.Id)));
        Task<Exception?> create = TryAsync(() => CreateLoadBalancerHandler(creating, tenant.Id).HandleAsync(
            new CreateLoadBalancerCommand($"lb-{Guid.NewGuid():N}"[..12], network.Id,
                [new LoadBalancerListenerInput("TCP", 80, 8080)], [new LoadBalancerBackendInput(vm.Id)])));
        Exception?[] outcomes = await Task.WhenAll(delete, create);

        Assert.Single(outcomes, o => o == null);
        BusinessException refused = Assert.IsType<BusinessException>(Assert.Single(outcomes, o => o != null));
        Assert.Contains(refused.Code, new[] { "VM_IS_LOAD_BALANCER_BACKEND", "LOAD_BALANCER_BACKEND_INVALID" });

        await using TitanDbContext read = pg.NewDbContext();
        Vm stored = (await new VmRepository(read).FindByIdAsync(vm.Id))!;
        bool vmIsGoing = stored.DesiredExistence == DesiredExistence.Deleted;
        bool aLoadBalancerSendsToIt = await new LoadBalancerRepository(read).IsBackendAsync(vm.Id);
        Assert.False(vmIsGoing && aLoadBalancerSendsToIt, "a load balancer sends to a VM being deleted");
    }

    private static async Task<Exception?> TryAsync(Func<Task> act)
    {
        try
        {
            await act();
            return null;
        }
        catch (Exception e)
        {
            return e;
        }
    }

    private static DeleteVmCommandHandler DeleteVmHandler(TitanDbContext db, Guid tenantId)
    {
        var caller = Callers.TenantMember(tenantId);
        return new DeleteVmCommandHandler(new VmRepository(db), new TenantRepository(db), caller,
            new ProviderCommandQueue(new ProviderCommandRepository(db), TimeProvider.System),
            new VolumeRepository(db), new ActivityRecorder(new ActivityEventRepository(db), caller),
            new VmDeletion(new LoadBalancerRepository(db)));
    }

    private static CreateLoadBalancerCommandHandler CreateLoadBalancerHandler(TitanDbContext db, Guid tenantId)
    {
        var caller = Callers.TenantMember(tenantId);
        return new CreateLoadBalancerCommandHandler(new LoadBalancerRepository(db), new NetworkRepository(db), new VmRepository(db),
            new TenantRepository(db), caller,
            new ProviderCommandQueue(new ProviderCommandRepository(db), TimeProvider.System),
            new ActivityRecorder(new ActivityEventRepository(db), caller));
    }
}
