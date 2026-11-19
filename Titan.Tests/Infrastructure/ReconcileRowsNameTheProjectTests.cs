using Titan.Application.Queries.Compute.Vm.Contracts;
using Titan.Application.Queries.Compute.VmSnapshot.Contracts;
using Titan.Application.Queries.Networking.Network.Contracts;
using Titan.Domain.Aggregates.Network;
using Titan.Domain.Aggregates.Tenant;
using Titan.Domain.Aggregates.Vm;
using Titan.Tests.Fakes;

namespace Titan.Tests.Infrastructure;

// The worker finds a VM, a snapshot's VM and a network in the project of their tenant, not by name across every project
// (titan-ops features/backup/design.md#incus-backup): a same-named instance or network in another project must not stand
// in for the tenant's. Each reconcile row therefore names that project. The storage rows are StorageReconcilerTests.
public class ReconcileRowsNameTheProjectTests
{
    private const string Cluster = "cluster-a";

    [Fact]
    public void AVmRow_NamesTheProjectOfItsTenant()
    {
        Tenant owner = TestTenant.WithRoomToSpare();
        Vm vm = Vm.Create(owner.Id, Guid.NewGuid(), "vm-1", "m", "tpl-1", Cluster, 20);
        vm.MarkRunning("vm0123456789abcdefghijklmnop", "10.0.0.5");

        ReconcilableVmQueryResult row = ReconcilableVmQueryResult.From(vm);

        Assert.Equal(Tenant.ProjectNameFor(owner.Id), row.ProjectName);
    }

    [Fact]
    public void AVmSnapshotRow_NamesTheProjectOfItsTenant()
    {
        Tenant owner = TestTenant.WithRoomToSpare();
        Vm vm = Vm.Create(owner.Id, Guid.NewGuid(), "vm-1", "m", "tpl-1", Cluster, 20);
        vm.MarkRunning("vm0123456789abcdefghijklmnop", "10.0.0.5");
        VmSnapshot snapshot = vm.WithSnapshot("snap");
        snapshot.MarkCreated("vm0123456789abcdefghijklmnop/s0123456789abcdefghijklmno");

        ReconcilableVmSnapshotQueryResult row = ReconcilableVmSnapshotQueryResult.From(snapshot);

        Assert.Equal(Tenant.ProjectNameFor(owner.Id), row.ProjectName);
    }

    [Fact]
    public void ANetworkRow_NamesTheProjectOfItsTenant()
    {
        Tenant owner = TestTenant.WithRoomToSpare();
        var network = TestAggregates.Network(owner, "net-a", Cluster);
        network.MarkCreated($"net-{network.Id}", "10.1.0.0/24", "10.1.0.1");

        ReconcilableNetworkQueryResult row = ReconcilableNetworkQueryResult.From(network);

        Assert.Equal(Tenant.ProjectNameFor(owner.Id), row.ProjectName);
    }
}
