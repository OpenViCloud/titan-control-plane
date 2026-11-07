using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Titan.Application.Queries.Compute.VmSnapshot;
using Titan.Application.Queries.Compute.VmSnapshot.Contracts;
using Titan.Application.Queries.Networking.FirewallRule;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Aggregates.Network;
using Titan.Infrastructure.Endpoints.Internal;
using Titan.Infrastructure.Hosting;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.Repositories;

namespace Titan.Tests.Infrastructure;

// The control-plane half of the firewall and VM snapshot reconcilers:
// GET /internal/reconcile/firewall-rules and /internal/reconcile/vm-snapshots return the rows of a cluster with the state
// Titan last recorded, what was asked, and the parent id the callback route needs; every call is the reconciler's
// heartbeat. The repository predicates are in VmSnapshotFirewallRepositoryStateTests.
public class FirewallAndVmSnapshotReconcilerTests
{
    private const string Cluster = "vn-hcm-1";

    private static IConfiguration ConfigWithToken(string token) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Internal:Token"] = token })
            .Build();

    private static TitanDbContext NewDb() =>
        new(new DbContextOptionsBuilder<TitanDbContext>()
            .UseInMemoryDatabase($"fw-vmsnap-reconcile-{Guid.NewGuid()}")
            .Options);

    private static void SendAs(Microsoft.AspNetCore.Http.HttpContext ctx, string token)
    {
        ctx.Request.Headers["X-Internal-Token"] = token;
        ctx.Request.QueryString = new Microsoft.AspNetCore.Http.QueryString($"?clusterId={Cluster}");
    }

    private static ListReconcilableFirewallRulesEndpoint FirewallEndpoint(TitanDbContext db, ReconcilerHeartbeat heartbeat, string token) =>
        Factory.Create<ListReconcilableFirewallRulesEndpoint>(
            ctx => SendAs(ctx, token),
            new ListReconcilableFirewallRulesQueryHandler(new FirewallRuleRepository(db)),
            heartbeat, ConfigWithToken("secret"));

    private static ListReconcilableVmSnapshotsEndpoint VmSnapshotEndpoint(TitanDbContext db, ReconcilerHeartbeat heartbeat, string token) =>
        Factory.Create<ListReconcilableVmSnapshotsEndpoint>(
            ctx => SendAs(ctx, token),
            new ListReconcilableVmSnapshotsQueryHandler(new VmSnapshotRepository(db)),
            heartbeat, ConfigWithToken("secret"));

    [Fact]
    public async Task FirewallEndpoint_WithToken_ReturnsRulesWithTheirNetwork_AndRecordsTheHeartbeat()
    {
        using var db = NewDb();
        var network = TestAggregates.Network(TestTenant.WithRoomToSpare(), "net", Cluster);
        var rule = FirewallRule.Create(Guid.NewGuid(), network.Id, network.TenantId, FirewallDirection.Ingress, FirewallProtocol.TCP, 22, 22, "0.0.0.0/0");
        rule.MarkActive("sg-7");
        rule.Delete();
        db.Networks.Add(network);
        db.FirewallRules.Add(rule);
        await db.SaveChangesAsync();
        var heartbeat = new ReconcilerHeartbeat();
        var ep = FirewallEndpoint(db, heartbeat, "secret");

        await ep.HandleAsync(CancellationToken.None);

        var r = Assert.Single(ep.Response);
        Assert.Equal(rule.Id, r.Id);
        Assert.Equal(network.Id, r.NetworkId);
        Assert.Equal("sg-7", r.ExternalId);
        Assert.Equal("enabled", r.CurrentState);
        Assert.Equal("Exists", r.CurrentExistence);
        Assert.NotNull(heartbeat.Snapshot()[$"firewall-rules/{Cluster}"].LastPollAt);
    }

    [Fact]
    public async Task VmSnapshotEndpoint_WithToken_ReturnsSnapshotsWithTheirVm_AndRecordsTheHeartbeat()
    {
        using var db = NewDb();
        var vm = Vm.Create(TestTenant.WithRoomToSpare().Id, Guid.NewGuid(), "vm-1", "m", "tpl-1", Cluster, 20);
        vm.MarkRunning("42", "10.0.0.5");
        var snapshot = vm.WithSnapshot("snap");
        snapshot.MarkCreated("42:3");
        vm.RevertSnapshot(snapshot.Id, Guid.NewGuid());
        db.Vms.Add(vm); // the snapshot comes with the VM
        await db.SaveChangesAsync();
        var heartbeat = new ReconcilerHeartbeat();
        var ep = VmSnapshotEndpoint(db, heartbeat, "secret");

        await ep.HandleAsync(CancellationToken.None);

        var s = Assert.Single(ep.Response);
        Assert.Equal(snapshot.Id, s.Id);
        Assert.Equal(vm.Id, s.VmId);
        Assert.Equal("42:3", s.ExternalId);
        Assert.Equal("Exists", s.CurrentExistence);
        Assert.NotNull(heartbeat.Snapshot()[$"vm-snapshots/{Cluster}"].LastPollAt);
    }

    // The worker's contract kept its existence word (titan-ops features/vm/design.md#vm-snapshot-reports): Exists while
    // Incus was last seen holding the snapshot, a revert or a delete in flight included; NotFound otherwise, so a
    // snapshot being deleted that Incus already lost is not reported again every cycle.
    [Theory]
    [InlineData("available", "Available", "Exists")]
    [InlineData("reverting", "Reverting", "Exists")]
    [InlineData("reverting-lost", "Reverting", "NotFound")]
    [InlineData("deleting", "Deleting", "Exists")]
    [InlineData("deleting-lost", "Deleting", "NotFound")]
    [InlineData("lost", "NotFound", "NotFound")]
    public void ReconcilableRow_GivesTheExistenceWord_FromTheStatusAndWhatIncusWasLastSeenHolding(
        string situation, string status, string existence)
    {
        var vm = Vm.Create(TestTenant.WithRoomToSpare().Id, Guid.NewGuid(), "vm-1", "m", "tpl-1", Cluster, 20);
        vm.MarkRunning("42", "10.0.0.5");
        var snapshot = vm.WithSnapshot("snap");
        snapshot.MarkCreated("42:3");
        switch (situation)
        {
            case "reverting":
                vm.RevertSnapshot(snapshot.Id, Guid.NewGuid());
                break;
            case "reverting-lost":
                vm.RevertSnapshot(snapshot.Id, Guid.NewGuid());
                vm.MarkSnapshotNotFound(snapshot.Id);
                break;
            case "deleting":
                vm.DeleteSnapshot(snapshot.Id, Guid.NewGuid());
                break;
            case "deleting-lost":
                vm.DeleteSnapshot(snapshot.Id, Guid.NewGuid());
                vm.MarkSnapshotNotFound(snapshot.Id);
                break;
            case "lost":
                vm.MarkSnapshotNotFound(snapshot.Id);
                break;
        }

        Assert.Equal(status, snapshot.Status.ToString());
        Assert.Equal(existence, ReconcilableVmSnapshotQueryResult.ExistenceName(snapshot));
        Assert.Equal(existence, ReconcilableVmSnapshotQueryResult.From(snapshot).CurrentExistence);
    }

    [Fact]
    public async Task FirewallEndpoint_WrongToken_Returns401_AndLeavesNoHeartbeat()
    {
        using var db = NewDb();
        var heartbeat = new ReconcilerHeartbeat();
        var ep = FirewallEndpoint(db, heartbeat, "wrong");

        await ep.HandleAsync(CancellationToken.None);

        Assert.Equal(401, ep.HttpContext.Response.StatusCode);
        Assert.Empty(heartbeat.Snapshot());
    }

    [Fact]
    public async Task VmSnapshotEndpoint_WrongToken_Returns401_AndLeavesNoHeartbeat()
    {
        using var db = NewDb();
        var heartbeat = new ReconcilerHeartbeat();
        var ep = VmSnapshotEndpoint(db, heartbeat, "wrong");

        await ep.HandleAsync(CancellationToken.None);

        Assert.Equal(401, ep.HttpContext.Response.StatusCode);
        Assert.Empty(heartbeat.Snapshot());
    }
}
