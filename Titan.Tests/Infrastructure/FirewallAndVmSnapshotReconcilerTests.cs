using FastEndpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Titan.Application.Queries.Compute.VmSnapshot;
using Titan.Application.Queries.Networking.FirewallRule;
using Titan.Domain.Aggregates.Compute.Vm;
using Titan.Domain.Aggregates.Compute.VmSnapshot;
using Titan.Domain.Aggregates.Networking.FirewallRule;
using Titan.Domain.Aggregates.Networking.Network;
using Titan.Infrastructure.Endpoints.Internal;
using Titan.Infrastructure.Hosting;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.Repositories;

namespace Titan.Tests.Infrastructure;

// The control-plane half of the firewall and VM snapshot reconcilers:
// GET /internal/reconcile/firewall-rules and /internal/reconcile/vm-snapshots return the rows of a zone with the state
// Titan last recorded, what was asked, and the parent id the callback route needs; every call is the reconciler's
// heartbeat. The repository predicates are in VmSnapshotFirewallRepositoryStateTests.
public class FirewallAndVmSnapshotReconcilerTests
{
    private const string Zone = "vn-hcm-1";

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
        ctx.Request.QueryString = new Microsoft.AspNetCore.Http.QueryString($"?clusterId={Zone}");
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
        var network = Network.CreateUnchecked(Guid.NewGuid(), "net", Zone);
        var rule = FirewallRule.CreateUnchecked(network.Id, network.TenantId, FirewallDirection.Ingress, FirewallProtocol.TCP, 22, 22, "0.0.0.0/0");
        rule.MarkActive("sg-7");
        rule.RequestDelete();
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
        Assert.NotNull(heartbeat.Snapshot()[$"firewall-rules/{Zone}"].LastPollAt);
    }

    [Fact]
    public async Task VmSnapshotEndpoint_WithToken_ReturnsSnapshotsWithTheirVm_AndRecordsTheHeartbeat()
    {
        using var db = NewDb();
        var vm = Vm.CreateUnchecked(Guid.NewGuid(), Guid.NewGuid(), "vm-1", "m", "tpl-1", Zone, 20);
        vm.MarkRunning("42", "10.0.0.5");
        var snapshot = VmSnapshot.CreateUnchecked(vm.Id, vm.TenantId, "snap");
        snapshot.MarkCreated("42:3");
        snapshot.Revert(vm);
        db.Vms.Add(vm);
        db.VmSnapshots.Add(snapshot);
        await db.SaveChangesAsync();
        var heartbeat = new ReconcilerHeartbeat();
        var ep = VmSnapshotEndpoint(db, heartbeat, "secret");

        await ep.HandleAsync(CancellationToken.None);

        var s = Assert.Single(ep.Response);
        Assert.Equal(snapshot.Id, s.Id);
        Assert.Equal(vm.Id, s.VmId);
        Assert.Equal("42:3", s.ExternalId);
        Assert.Equal("Exists", s.CurrentExistence);
        Assert.NotNull(heartbeat.Snapshot()[$"vm-snapshots/{Zone}"].LastPollAt);
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
