using Microsoft.EntityFrameworkCore;
using Titan.Application.Commands.Compute.Vm;
using Titan.Application.Commands.Compute.Vm.Contracts;
using Titan.Application.Commands.Networking.Network;
using Titan.Application.Commands.Networking.Network.Contracts;
using Titan.Application.Options;
using Titan.Application.Services.Platform.ActivityEvent;
using Titan.Domain.Aggregates.Network;
using Titan.Domain.Aggregates.Tenant;
using Titan.Domain.Aggregates.VmTemplate;
using Titan.Domain.Exceptions;
using Titan.Domain.Services;
using Titan.Domain.Values;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.ProviderCommands;
using Titan.Infrastructure.Repositories;

namespace Titan.Tests.Infrastructure.Integration;

/// A network still carrying a VM is not deleted (titan-ops architecture/domain-services.md#cross-aggregate-lock): the
/// delete and a VM create on the same network take turns on the network's row, so the second sees the first. Without
/// the lock both can read the other's state before it commits and both commit; the two requests are only started
/// together, not forced to interleave, so a run without the lock may also pass. Category=Integration — needs real
/// Postgres, since the lock is a Postgres row lock (titan-ops architecture/testing.md#doubles-only-in-unit-tests).
[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public class NetworkDeletionRaceDbTests(PostgresFixture pg)
{
    private const string Cluster = "cluster-1";

    [Fact]
    public async Task DeleteAndAVmCreateOnTheSameNetwork_OneIsRefused_AndNoVmLivesOnADeletedNetwork()
    {
        Tenant tenant = TestTenant.Active($"nd-race-{Guid.NewGuid():N}"[..20], $"kc-{Guid.NewGuid():N}", "nd-race@titan.local");
        Network network = TestAggregates.Network(tenant, $"net-{Guid.NewGuid():N}"[..12], Cluster);
        network.MarkActive($"net{Guid.NewGuid():N}"[..20], "10.0.0.0/24", "10.0.0.1");
        VmTemplate template = VmTemplate.Create($"tpl-{Guid.NewGuid():N}"[..12], null, "Ubuntu-22.04", 20, "l");
        template.SyncIn(Cluster);
        template.MarkCreatedIn(Cluster, $"tpl-{Guid.NewGuid():N}"[..20]);
        await using (TitanDbContext seed = pg.NewDbContext())
        {
            seed.Tenants.Add(tenant);
            seed.Networks.Add(network);
            seed.VmTemplates.Add(template);
            await seed.SaveChangesAsync();
        }

        // Two requests, two contexts, started together: without the lock each reads the other's side before it commits.
        await using TitanDbContext deleting = pg.NewDbContext();
        await using TitanDbContext creating = pg.NewDbContext();
        Task<Exception?> delete = TryAsync(() => DeleteHandler(deleting, tenant.Id).HandleAsync(new DeleteNetworkCommand(network.Id)));
        Task<Exception?> create = TryAsync(() => CreateVmHandler(creating, tenant.Id).HandleAsync(new CreateVmCommand(
            $"vm-{Guid.NewGuid():N}"[..12], network.Id, "m", template.Id.ToString(), Cluster, null)));
        Exception?[] outcomes = await Task.WhenAll(delete, create);

        Assert.Single(outcomes, o => o == null);
        BusinessException refused = Assert.IsType<BusinessException>(Assert.Single(outcomes, o => o != null));
        Assert.Contains(refused.Code, new[] { "NETWORK_NOT_EMPTY", "NETWORK_NOT_ACTIVE" });

        await using TitanDbContext read = pg.NewDbContext();
        Network stored = (await new NetworkRepository(read).FindByIdAsync(network.Id))!;
        int vmsOnIt = await read.Vms.CountAsync(v => v.NetworkId == network.Id);
        bool networkIsGoing = stored.DesiredExistence == DesiredExistence.Deleted;
        Assert.False(networkIsGoing && vmsOnIt > 0, $"network going away with {vmsOnIt} VM on it");
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

    private static DeleteNetworkCommandHandler DeleteHandler(TitanDbContext db, Guid tenantId)
    {
        var caller = Callers.TenantMember(tenantId);
        var networks = new NetworkRepository(db);
        return new DeleteNetworkCommandHandler(networks, caller,
            new ProviderCommandQueue(new ProviderCommandRepository(db), TimeProvider.System),
            new ActivityRecorder(new ActivityEventRepository(db), caller), new NetworkDeletion(networks));
    }

    private static CreateVmCommandHandler CreateVmHandler(TitanDbContext db, Guid tenantId)
    {
        var caller = Callers.TenantMember(tenantId);
        return new CreateVmCommandHandler(new VmRepository(db), new NetworkRepository(db), new TenantRepository(db), caller,
            new ProviderCommandQueue(new ProviderCommandRepository(db), TimeProvider.System),
            new VmOfferingRepository(db), new VmTemplateRepository(db), new SshKeyRepository(db),
            new ActivityRecorder(new ActivityEventRepository(db), caller),
            new ClusterOptions { Clusters = [new() { Id = Cluster, Name = "Cluster 1" }] });
    }
}
