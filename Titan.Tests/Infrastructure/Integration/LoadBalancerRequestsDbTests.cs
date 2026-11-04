using Microsoft.EntityFrameworkCore;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.LoadBalancer;
using Titan.Application.Commands.Compute.LoadBalancer.Contracts;
using Titan.Application.Services.Platform.ActivityEvent;
using Titan.Domain.Aggregates.LoadBalancer;
using Titan.Domain.Aggregates.ProviderCommand;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Aggregates.VmOffering;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.ProviderCommands;
using Titan.Infrastructure.Repositories;

namespace Titan.Tests.Infrastructure.Integration;

/// What only Postgres can show of titan-ops architecture/resource-lifecycle.md for load balancers: the row lock that makes
/// two requests on one load balancer take turns (§one-key-per-request), and the stuck-request query joining its request
/// to its command row (§lost-and-stale). Category=Integration — its own CI step.
[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public class LoadBalancerRequestsDbTests(PostgresFixture pg)
{
    private const string Subnet = "2001:db8:0:5::/64";
    private static readonly Guid Tenant = TestTenant.WithRoomToSpare().Id;

    // A load balancer Incus holds, sending to one stored VM; nothing waits.
    private async Task<(LoadBalancer LoadBalancer, Vm Backend)> StoredLoadBalancerAsync()
    {
        string offeringId = await SeedOfferingAsync();
        Guid networkId = Guid.NewGuid();
        Vm vm = Vm.Create(Tenant, networkId, $"vm-{Guid.NewGuid():N}"[..12], offeringId, "tpl-1", "cluster-a", 20);
        vm.EndOperation(vm.Pending!.Id,
            VmOperationEnd.Done($"vm{Guid.NewGuid():N}"[..20], new VmObservation(VmState.Running, "10.0.0.5", null)));
        LoadBalancer lb = LoadBalancer.Create(Tenant, $"lb-{Guid.NewGuid():N}"[..12], networkId, "net-x", Subnet, "cluster-a",
            LoadBalancerVip.For(Subnet, 1), [new LoadBalancerListenerSpec(LoadBalancerProtocol.TCP, 80, 8080)], [vm]);
        lb.EndOperation(lb.Pending!.Id, LoadBalancerOperationEnd.Done(lb.Vip));
        await using TitanDbContext db = pg.NewDbContext();
        db.Vms.Add(vm);
        db.LoadBalancers.Add(lb);
        await db.SaveChangesAsync();
        return (lb, vm);
    }

    private async Task<string> SeedOfferingAsync()
    {
        await using TitanDbContext db = pg.NewDbContext();
        VmOffering? existing = await db.VmOfferings.FirstOrDefaultAsync();
        return existing?.Id ?? throw new InvalidOperationException("The migrated database seeds the VM offering catalog; none found.");
    }

    private async Task UpdateThroughTheHandlerAsync(Guid loadBalancerId, Guid backendId)
    {
        await using TitanDbContext db = pg.NewDbContext();
        ICurrentUser caller = Callers.TenantMember(Tenant);
        var handler = new UpdateLoadBalancerCommandHandler(new LoadBalancerRepository(db), new VmRepository(db), caller,
            new ProviderCommandQueue(new ProviderCommandRepository(db), TimeProvider.System),
            new ActivityRecorder(new ActivityEventRepository(db), caller));
        await handler.HandleAsync(new UpdateLoadBalancerCommand(loadBalancerId,
            [new LoadBalancerListenerInput("UDP", 53, 53)], [new LoadBalancerBackendInput(backendId)]));
    }

    private async Task DeleteThroughTheHandlerAsync(Guid loadBalancerId)
    {
        await using TitanDbContext db = pg.NewDbContext();
        ICurrentUser caller = Callers.TenantMember(Tenant);
        var handler = new DeleteLoadBalancerCommandHandler(new LoadBalancerRepository(db), caller,
            new ProviderCommandQueue(new ProviderCommandRepository(db), TimeProvider.System),
            new ActivityRecorder(new ActivityEventRepository(db), caller));
        await handler.HandleAsync(new DeleteLoadBalancerCommand(loadBalancerId));
    }

    private async Task<LoadBalancer> StoredAsync(Guid id)
    {
        await using TitanDbContext db = pg.NewDbContext();
        return (await new LoadBalancerRepository(db).FindByIdAsync(id))!;
    }

    private async Task<List<string>> KeysAsync(string prefix)
    {
        await using TitanDbContext db = pg.NewDbContext();
        return await db.Set<ProviderCommand>().Where(c => c.Key.StartsWith(prefix)).Select(c => c.Key).ToListAsync();
    }

    // The second request waits on the row lock, then sees the first waiting and is a repeat: one request, one command
    // row, neither scope fails (titan-ops architecture/resource-lifecycle.md#one-key-per-request).
    [Fact]
    public async Task TwoUpdatesOfOneLoadBalancer_AtOnce_LeaveOneRequest_AndOneCommandRow()
    {
        (LoadBalancer lb, Vm backend) = await StoredLoadBalancerAsync();

        await Task.WhenAll(UpdateThroughTheHandlerAsync(lb.Id, backend.Id), UpdateThroughTheHandlerAsync(lb.Id, backend.Id));

        LoadBalancer stored = await StoredAsync(lb.Id);
        Assert.Equal(LoadBalancerOperationKind.Update, stored.Pending!.Kind);
        Assert.Equal([$"load-balancer-update-{lb.Id}-{stored.Pending.Id}"], await KeysAsync($"load-balancer-update-{lb.Id}-"));
    }

    [Fact]
    public async Task TwoDeletesOfOneLoadBalancer_AtOnce_LeaveOneRequest_AndOneCommandRow()
    {
        (LoadBalancer lb, _) = await StoredLoadBalancerAsync();

        await Task.WhenAll(DeleteThroughTheHandlerAsync(lb.Id), DeleteThroughTheHandlerAsync(lb.Id));

        LoadBalancer stored = await StoredAsync(lb.Id);
        Assert.Equal(LoadBalancerOperationKind.Delete, stored.Pending!.Kind);
        Assert.Equal([$"load-balancer-delete-{lb.Id}-{stored.Pending.Id}"], await KeysAsync($"load-balancer-delete-{lb.Id}-"));
    }

    [Fact]
    public async Task AStuckLoadBalancerRequest_IsListedWithItsCommandRowsStatus()
    {
        (LoadBalancer lb, _) = await StoredLoadBalancerAsync();
        await DeleteThroughTheHandlerAsync(lb.Id);
        await using (TitanDbContext db = pg.NewDbContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE core.load_balancers SET pending_request_asked_at = now() - make_interval(mins => 20) WHERE id = {lb.Id}");
        }

        await using TitanDbContext read = pg.NewDbContext();
        IReadOnlyList<StuckRequest> stuck = await new PendingRequestReader(read).ListStuckAsync(TimeSpan.FromMinutes(15));

        StuckRequest delete = Assert.Single(stuck, s => s.Id == lb.Id);
        Assert.Equal(("load-balancer", "Delete", "Pending", true), (delete.Resource, delete.Kind, delete.CommandStatus, delete.CommandIsOpen));
    }
}
