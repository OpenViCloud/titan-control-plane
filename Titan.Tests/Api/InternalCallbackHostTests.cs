using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Titan.Domain.Aggregates.Network;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Aggregates.Volume;
using Titan.Domain.Values;
using Titan.Infrastructure.Persistence;

namespace Titan.Tests.Api;

/// The worker's callbacks as the worker sends them (titan-ops architecture/resource-lifecycle.md#callback-routes): routing,
/// the X-Internal-Token check, and the camelCase JSON body bound by the host, which a test calling HandleAsync never
/// reaches. Each test stores its own resource, so the tests share the host and its database.
// FullHost — its own CI process, for the reason CatalogContractTests gives.
[Trait("Category", "FullHost")]
[Collection("Http")]
public class InternalCallbackHostTests(InternalCallbackHostTests.CallbackHostFactory factory)
    : IClassFixture<InternalCallbackHostTests.CallbackHostFactory>
{
    private const string InternalToken = "test-token";
    private const string Offering = "m-callback";

    private async Task<Vm> StoredVmWaitingOnAStopAsync()
    {
        Vm vm = Vm.Create(TestTenant.WithRoomToSpare().Id, Guid.NewGuid(), $"vm-{Guid.NewGuid():N}"[..14], Offering, "tpl", "cluster-a", 20);
        vm.MarkRunning($"ext-{vm.Id:N}", "10.0.0.5");
        vm.RequestStop();
        using IServiceScope scope = factory.Services.CreateScope();
        TitanDbContext db = scope.ServiceProvider.GetRequiredService<TitanDbContext>();
        await db.Database.ExecuteSqlRawAsync(
            $"INSERT INTO core.vm_offerings (id, name, cpu_units, vcpu_count, memory_mb, price_per_hour) VALUES ('{Offering}', 'm', 1, 2, 4096, 311.256) ON CONFLICT (id) DO NOTHING");
        db.Vms.Add(vm);
        await db.SaveChangesAsync();
        return vm;
    }

    private async Task<Vm> ReloadAsync(Guid vmId)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        TitanDbContext db = scope.ServiceProvider.GetRequiredService<TitanDbContext>();
        return await db.Vms.AsNoTracking().SingleAsync(v => v.Id == vmId);
    }

    private async Task<HttpResponseMessage> PatchAsync(string path, string json, string? token = InternalToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        if (token != null)
        {
            request.Headers.Add("X-Internal-Token", token);
        }
        return await factory.CreateClient().SendAsync(request);
    }

    [Fact]
    public async Task ADoneNamingTheWaitingRequest_InTheWorkersJson_EndsIt_AndAnswers204()
    {
        Vm vm = await StoredVmWaitingOnAStopAsync();
        Guid requestId = vm.Pending!.Id;

        HttpResponseMessage response = await PatchAsync($"/internal/vms/{vm.Id}/requests/{requestId}",
            """{"outcome":"Done","observed":{"status":"Stopped","ipAddress":null}}""");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Vm stored = await ReloadAsync(vm.Id);
        Assert.Null(stored.Pending);
        Assert.Equal(VmState.Stopped, stored.CurrentState);
    }

    [Fact]
    public async Task AFailedWithItsReason_InTheWorkersJson_PutsDesiredBack_AndKeepsTheReason()
    {
        Vm vm = await StoredVmWaitingOnAStopAsync();
        Guid requestId = vm.Pending!.Id;

        HttpResponseMessage response = await PatchAsync($"/internal/vms/{vm.Id}/requests/{requestId}",
            """{"outcome":"Failed","reason":"The stop could not be completed.","detail":"incus: timeout"}""");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Vm stored = await ReloadAsync(vm.Id);
        Assert.Null(stored.Pending);
        Assert.Equal(VmDesiredState.Running, stored.DesiredState);
        Assert.Equal(("The stop could not be completed.", "incus: timeout"), (stored.RequestFailureReason, stored.RequestFailureDetail));
    }

    [Fact]
    public async Task AnObservation_InTheWorkersJson_WritesCurrent_AndAnswers204()
    {
        Vm vm = await StoredVmWaitingOnAStopAsync();

        HttpResponseMessage response = await PatchAsync($"/internal/vms/{vm.Id}/observation",
            """{"exists":true,"observed":{"status":"Frozen","ipAddress":"10.0.0.9","publicIpv6Address":"2001:db8::9"}}""");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Vm stored = await ReloadAsync(vm.Id);
        Assert.Equal((VmState.Frozen, "10.0.0.9", "2001:db8::9"), (stored.CurrentState, stored.IpAddress, stored.PublicIpv6Address));
        Assert.Equal(VmRequestKind.Stop, stored.Pending!.Kind);
    }

    [Theory]
    [InlineData("""{"outcome":"Maybe"}""")]
    [InlineData("""{"outcome":"Done","observed":{"status":"Sleeping"}}""")]
    public async Task AReportTheControlPlaneCannotRead_Is400_WithItsCode_AndChangesNothing(string json)
    {
        Vm vm = await StoredVmWaitingOnAStopAsync();

        HttpResponseMessage response = await PatchAsync($"/internal/vms/{vm.Id}/requests/{vm.Pending!.Id}", json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using JsonDocument envelope = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("INVALID_STATUS", envelope.RootElement.GetProperty("code").GetString());
        Assert.Equal(VmRequestKind.Stop, (await ReloadAsync(vm.Id)).Pending!.Kind);
    }

    [Fact]
    public async Task ABodyThatIsNotTheReportsShape_Is400_AndChangesNothing()
    {
        Vm vm = await StoredVmWaitingOnAStopAsync();

        HttpResponseMessage response = await PatchAsync($"/internal/vms/{vm.Id}/observation", """{"exists":"yes"}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Vm stored = await ReloadAsync(vm.Id);
        Assert.Equal(VmState.Running, stored.CurrentState);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-token")]
    public async Task WithoutTheToken_Is401_AndChangesNothing(string? token)
    {
        Vm vm = await StoredVmWaitingOnAStopAsync();

        HttpResponseMessage response = await PatchAsync($"/internal/vms/{vm.Id}/requests/{vm.Pending!.Id}",
            """{"outcome":"Done","observed":{"status":"Stopped"}}""", token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(VmRequestKind.Stop, (await ReloadAsync(vm.Id)).Pending!.Kind);
    }

    // ---------- the volume's and its snapshots' routes ----------

    private const string VolumeOffering = "vol-callback";

    private async Task<Volume> StoredVolumeWaitingOnADeleteAsync()
    {
        Volume volume = Volume.Create(TestTenant.WithRoomToSpare().Id, $"vol-{Guid.NewGuid():N}"[..14], "cluster-a", VolumeOffering, 10);
        volume.EndRequest(volume.Pending!.Id, VolumeRequestEnd.Done($"vol-{volume.Id:N}", null));
        VolumeSnapshot snapshot = volume.AddSnapshot("s1").Snapshot;
        volume.EndSnapshotRequest(snapshot.Id, snapshot.Pending!.Id, VolumeSnapshotRequestEnd.Done($"vol-{volume.Id:N}/s1"));
        volume.RequestSnapshotDelete(snapshot.Id);
        volume.RequestDelete();
        using IServiceScope scope = factory.Services.CreateScope();
        TitanDbContext db = scope.ServiceProvider.GetRequiredService<TitanDbContext>();
        await db.Database.ExecuteSqlRawAsync(
            $"INSERT INTO core.volume_offerings (id, name, size_gb, is_customized, price_per_gb_per_hour) VALUES ('{VolumeOffering}', 'v', 10, false, 0.0001) ON CONFLICT (id) DO NOTHING");
        db.Volumes.Add(volume);
        await db.SaveChangesAsync();
        return volume;
    }

    private async Task<Volume> ReloadVolumeAsync(Guid volumeId)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        TitanDbContext db = scope.ServiceProvider.GetRequiredService<TitanDbContext>();
        return await db.Volumes.AsNoTracking().Include(v => v.Snapshots).SingleAsync(v => v.Id == volumeId);
    }

    [Fact]
    public async Task AVolumeFailed_InTheWorkersJson_GivesTheVolumeBack_WithItsReason()
    {
        Volume volume = await StoredVolumeWaitingOnADeleteAsync();

        HttpResponseMessage response = await PatchAsync($"/internal/volumes/{volume.Id}/requests/{volume.Pending!.Id}",
            """{"outcome":"Failed","observed":{"attachedTo":[]},"reason":"The volume could not be deleted.","detail":"incus: busy"}""");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Volume stored = await ReloadVolumeAsync(volume.Id);
        Assert.Null(stored.Pending);
        Assert.Equal(DesiredExistence.Exists, stored.DesiredExistence);
        Assert.Equal("The volume could not be deleted.", stored.RequestFailureReason);
    }

    [Fact]
    public async Task AVolumeObservation_InTheWorkersJson_WritesCurrent_AndLeavesTheDeleteWaiting()
    {
        Volume volume = await StoredVolumeWaitingOnADeleteAsync();

        HttpResponseMessage response = await PatchAsync($"/internal/volumes/{volume.Id}/observation",
            """{"exists":true,"observed":{"attachedTo":["an-instance-outside-titan"]}}""");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Volume stored = await ReloadVolumeAsync(volume.Id);
        Assert.True(stored.CurrentAttachedElsewhere);
        Assert.Equal(VolumeRequestKind.Delete, stored.Pending!.Kind);
    }

    [Fact]
    public async Task ASnapshotDone_InTheWorkersJson_EndsItsDelete()
    {
        Volume volume = await StoredVolumeWaitingOnADeleteAsync();
        VolumeSnapshot snapshot = volume.Snapshots.Single();

        HttpResponseMessage response = await PatchAsync(
            $"/internal/volumes/{volume.Id}/snapshots/{snapshot.Id}/requests/{snapshot.Pending!.Id}", """{"outcome":"Done"}""");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(CurrentExistence.Deleted, (await ReloadVolumeAsync(volume.Id)).Snapshots.Single().CurrentExistence);
    }

    [Fact]
    public async Task ASnapshotObservation_InTheWorkersJson_WritesCurrent_AndLeavesItsDeleteWaiting()
    {
        Volume volume = await StoredVolumeWaitingOnADeleteAsync();
        VolumeSnapshot snapshot = volume.Snapshots.Single();

        HttpResponseMessage response = await PatchAsync(
            $"/internal/volumes/{volume.Id}/snapshots/{snapshot.Id}/observation", """{"exists":false}""");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        VolumeSnapshot stored = (await ReloadVolumeAsync(volume.Id)).Snapshots.Single();
        Assert.Equal(CurrentExistence.NotFound, stored.CurrentExistence);
        Assert.Equal(VolumeSnapshotRequestKind.Delete, stored.Pending!.Kind);
    }

    [Theory]
    [InlineData("requests/{0}", """{"outcome":"Maybe"}""")]
    [InlineData("observation", """{"exists":"yes"}""")]
    public async Task AVolumeReportTheControlPlaneCannotRead_Is400_AndChangesNothing(string route, string json)
    {
        Volume volume = await StoredVolumeWaitingOnADeleteAsync();

        HttpResponseMessage response = await PatchAsync($"/internal/volumes/{volume.Id}/{string.Format(route, volume.Pending!.Id)}", json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(VolumeRequestKind.Delete, (await ReloadVolumeAsync(volume.Id)).Pending!.Kind);
    }

    [Theory]
    [InlineData("requests/{0}", """{"outcome":"Done"}""")]
    [InlineData("observation", """{"exists":false}""")]
    [InlineData("snapshots/{1}/requests/{2}", """{"outcome":"Done"}""")]
    [InlineData("snapshots/{1}/observation", """{"exists":false}""")]
    public async Task EveryVolumeRoute_WithoutTheToken_Is401_AndChangesNothing(string route, string json)
    {
        Volume volume = await StoredVolumeWaitingOnADeleteAsync();
        VolumeSnapshot snapshot = volume.Snapshots.Single();

        HttpResponseMessage response = await PatchAsync(
            $"/internal/volumes/{volume.Id}/{string.Format(route, volume.Pending!.Id, snapshot.Id, snapshot.Pending!.Id)}", json, token: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Volume stored = await ReloadVolumeAsync(volume.Id);
        Assert.Equal((CurrentExistence.Exists, VolumeRequestKind.Delete), (stored.CurrentExistence, stored.Pending!.Kind));
        Assert.Equal(CurrentExistence.Exists, stored.Snapshots.Single().CurrentExistence);
    }

    // ---------- the network's and its firewall rules' routes ----------

    private async Task<Network> StoredNetworkAndRuleWaitingOnTheirDeletesAsync()
    {
        Network network = Network.Create(TestTenant.WithRoomToSpare().Id, NetworkName.Parse($"net-{Guid.NewGuid():N}"[..14]),
            "cluster-a", null, wantsPublicIpv6: false);
        network.EndRequest(network.Pending!.Id,
            NetworkRequestEnd.Done($"net-{network.Id:N}", new NetworkObservation(NetworkStatus.Created, "10.0.0.0/24", "10.0.0.1")));
        FirewallRule rule = network.AddFirewallRule(FirewallDirection.Ingress, FirewallProtocol.TCP, null, FirewallCidr.Parse("10.0.0.0/8")).Rule;
        network.EndFirewallRuleRequest(rule.Id, rule.Pending!.Id, FirewallRuleRequestEnd.Done($"acl-{network.Id:N}/1", FirewallRuleState.enabled));
        network.RequestFirewallRuleDelete(rule.Id);
        network.RequestDelete();
        using IServiceScope scope = factory.Services.CreateScope();
        TitanDbContext db = scope.ServiceProvider.GetRequiredService<TitanDbContext>();
        db.Networks.Add(network);
        await db.SaveChangesAsync();
        return network;
    }

    private async Task<Network> ReloadNetworkAsync(Guid networkId)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        TitanDbContext db = scope.ServiceProvider.GetRequiredService<TitanDbContext>();
        return await db.Networks.AsNoTracking().Include(n => n.FirewallRules).SingleAsync(n => n.Id == networkId);
    }

    [Fact]
    public async Task ANetworkFailed_InTheWorkersJson_GivesTheNetworkBack_WithItsReason()
    {
        Network network = await StoredNetworkAndRuleWaitingOnTheirDeletesAsync();

        HttpResponseMessage response = await PatchAsync($"/internal/networks/{network.Id}/requests/{network.Pending!.Id}",
            """{"outcome":"Failed","observed":{"status":"Created"},"reason":"The network could not be deleted.","detail":"incus: in use"}""");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Network stored = await ReloadNetworkAsync(network.Id);
        Assert.Null(stored.Pending);
        Assert.Equal(DesiredExistence.Exists, stored.DesiredExistence);
        Assert.Equal(("The network could not be deleted.", "incus: in use"), (stored.RequestFailureReason, stored.RequestFailureDetail));
    }

    [Fact]
    public async Task ANetworkDone_InTheWorkersJson_EndsItsDelete_AndTheRulesWithIt()
    {
        Network network = await StoredNetworkAndRuleWaitingOnTheirDeletesAsync();

        HttpResponseMessage response = await PatchAsync($"/internal/networks/{network.Id}/requests/{network.Pending!.Id}",
            """{"outcome":"Done"}""");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Network stored = await ReloadNetworkAsync(network.Id);
        Assert.Null(stored.Pending);
        Assert.Equal(CurrentExistence.Deleted, stored.CurrentExistence);
        Assert.Equal(CurrentExistence.Deleted, stored.FirewallRules.Single().CurrentExistence);
    }

    [Fact]
    public async Task ANetworkObservation_InTheWorkersJson_WritesCurrent_AndLeavesTheDeleteWaiting()
    {
        Network network = await StoredNetworkAndRuleWaitingOnTheirDeletesAsync();

        HttpResponseMessage response = await PatchAsync($"/internal/networks/{network.Id}/observation",
            """{"exists":true,"observed":{"status":"Errored"}}""");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Network stored = await ReloadNetworkAsync(network.Id);
        Assert.Equal(NetworkStatus.Errored, stored.CurrentState);
        Assert.Equal(NetworkRequestKind.Delete, stored.Pending!.Kind);
    }

    [Fact]
    public async Task ARuleDone_InTheWorkersJson_EndsItsDelete()
    {
        Network network = await StoredNetworkAndRuleWaitingOnTheirDeletesAsync();
        FirewallRule rule = network.FirewallRules.Single();

        HttpResponseMessage response = await PatchAsync(
            $"/internal/networks/{network.Id}/firewall-rules/{rule.Id}/requests/{rule.Pending!.Id}", """{"outcome":"Done"}""");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Network stored = await ReloadNetworkAsync(network.Id);
        Assert.Equal(CurrentExistence.Deleted, stored.FirewallRules.Single().CurrentExistence);
        Assert.Equal(NetworkRequestKind.Delete, stored.Pending!.Kind);
    }

    [Fact]
    public async Task ARuleObservation_InTheWorkersJson_WritesCurrent_AndLeavesItsDeleteWaiting()
    {
        Network network = await StoredNetworkAndRuleWaitingOnTheirDeletesAsync();
        FirewallRule rule = network.FirewallRules.Single();

        HttpResponseMessage response = await PatchAsync(
            $"/internal/networks/{network.Id}/firewall-rules/{rule.Id}/observation", """{"exists":true,"observed":{"state":"disabled"}}""");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        FirewallRule stored = (await ReloadNetworkAsync(network.Id)).FirewallRules.Single();
        Assert.Equal(FirewallRuleState.disabled, stored.CurrentState);
        Assert.Equal(FirewallRuleRequestKind.Delete, stored.Pending!.Kind);
    }

    [Theory]
    [InlineData("requests/{0}", """{"outcome":"Maybe"}""")]
    [InlineData("requests/{0}", """{"outcome":"Done","observed":{"status":"Sleeping"}}""")]
    [InlineData("observation", """{"exists":"yes"}""")]
    [InlineData("firewall-rules/{1}/requests/{2}", """{"outcome":"Maybe"}""")]
    [InlineData("firewall-rules/{1}/requests/{2}", """{"outcome":"Done","observed":{"state":"sleeping"}}""")]
    [InlineData("firewall-rules/{1}/observation", """{"exists":"yes"}""")]
    public async Task ANetworkOrRuleReportTheControlPlaneCannotRead_Is400_AndChangesNothing(string route, string json)
    {
        Network network = await StoredNetworkAndRuleWaitingOnTheirDeletesAsync();
        FirewallRule rule = network.FirewallRules.Single();

        HttpResponseMessage response = await PatchAsync(
            $"/internal/networks/{network.Id}/{string.Format(route, network.Pending!.Id, rule.Id, rule.Pending!.Id)}", json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Network stored = await ReloadNetworkAsync(network.Id);
        Assert.Equal((NetworkStatus.Created, NetworkRequestKind.Delete), (stored.CurrentState!.Value, stored.Pending!.Kind));
        Assert.Equal((FirewallRuleState.enabled, FirewallRuleRequestKind.Delete),
            (stored.FirewallRules.Single().CurrentState!.Value, stored.FirewallRules.Single().Pending!.Kind));
    }

    [Theory]
    [InlineData("requests/{0}", """{"outcome":"Done"}""")]
    [InlineData("observation", """{"exists":false}""")]
    [InlineData("firewall-rules/{1}/requests/{2}", """{"outcome":"Done"}""")]
    [InlineData("firewall-rules/{1}/observation", """{"exists":false}""")]
    public async Task EveryNetworkRoute_WithoutTheToken_Is401_AndChangesNothing(string route, string json)
    {
        Network network = await StoredNetworkAndRuleWaitingOnTheirDeletesAsync();
        FirewallRule rule = network.FirewallRules.Single();

        HttpResponseMessage response = await PatchAsync(
            $"/internal/networks/{network.Id}/{string.Format(route, network.Pending!.Id, rule.Id, rule.Pending!.Id)}", json, token: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Network stored = await ReloadNetworkAsync(network.Id);
        Assert.Equal((CurrentExistence.Exists, NetworkRequestKind.Delete), (stored.CurrentExistence, stored.Pending!.Kind));
        Assert.Equal((CurrentExistence.Exists, FirewallRuleRequestKind.Delete),
            (stored.FirewallRules.Single().CurrentExistence, stored.FirewallRules.Single().Pending!.Kind));
    }

    // ---------- the VM's snapshots' routes ----------

    // A running VM with a snapshot waiting on its create, and two Incus holds: one waiting on its revert, one on its delete.
    private async Task<Vm> StoredVmWithSnapshotsWaitingAsync()
    {
        Vm vm = Vm.Create(TestTenant.WithRoomToSpare().Id, Guid.NewGuid(), $"vm-{Guid.NewGuid():N}"[..14], Offering, "tpl", "cluster-a", 20);
        vm.MarkRunning($"ext-{vm.Id:N}", "10.0.0.5");
        VmSnapshot reverting = vm.AddSnapshot("reverting").Snapshot;
        vm.EndSnapshotRequest(reverting.Id, reverting.Pending!.Id, VmSnapshotRequestEnd.Done($"ext-{vm.Id:N}/s1"));
        vm.RevertSnapshot(reverting.Id);
        VmSnapshot deleting = vm.AddSnapshot("deleting").Snapshot;
        vm.EndSnapshotRequest(deleting.Id, deleting.Pending!.Id, VmSnapshotRequestEnd.Done($"ext-{vm.Id:N}/s2"));
        vm.RequestSnapshotDelete(deleting.Id);
        vm.AddSnapshot("creating");
        using IServiceScope scope = factory.Services.CreateScope();
        TitanDbContext db = scope.ServiceProvider.GetRequiredService<TitanDbContext>();
        await db.Database.ExecuteSqlRawAsync(
            $"INSERT INTO core.vm_offerings (id, name, cpu_units, vcpu_count, memory_mb, price_per_hour) VALUES ('{Offering}', 'm', 1, 2, 4096, 311.256) ON CONFLICT (id) DO NOTHING");
        db.Vms.Add(vm);
        await db.SaveChangesAsync();
        return vm;
    }

    private async Task<VmSnapshot> ReloadSnapshotAsync(Guid vmId, string name)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        TitanDbContext db = scope.ServiceProvider.GetRequiredService<TitanDbContext>();
        Vm vm = await db.Vms.AsNoTracking().Include(v => v.Snapshots).SingleAsync(v => v.Id == vmId);
        return vm.Snapshots.Single(s => s.Name == name);
    }

    [Fact]
    public async Task AVmSnapshotFailed_InTheWorkersJson_GivesTheSnapshotBack_WithItsReason()
    {
        Vm vm = await StoredVmWithSnapshotsWaitingAsync();
        VmSnapshot deleting = vm.Snapshots.Single(s => s.Name == "deleting");

        HttpResponseMessage response = await PatchAsync(
            $"/internal/vms/{vm.Id}/snapshots/{deleting.Id}/requests/{deleting.Pending!.Id}",
            """{"outcome":"Failed","reason":"The snapshot could not be deleted.","detail":"incus: busy"}""");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        VmSnapshot stored = await ReloadSnapshotAsync(vm.Id, "deleting");
        Assert.Null(stored.Pending);
        Assert.Equal(DesiredExistence.Exists, stored.DesiredExistence);
        Assert.Equal("The snapshot could not be deleted.", stored.RequestFailureReason);
    }

    [Fact]
    public async Task AVmSnapshotDone_InTheWorkersJson_EndsItsRevert()
    {
        Vm vm = await StoredVmWithSnapshotsWaitingAsync();
        VmSnapshot reverting = vm.Snapshots.Single(s => s.Name == "reverting");

        HttpResponseMessage response = await PatchAsync(
            $"/internal/vms/{vm.Id}/snapshots/{reverting.Id}/requests/{reverting.Pending!.Id}", """{"outcome":"Done"}""");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        VmSnapshot stored = await ReloadSnapshotAsync(vm.Id, "reverting");
        Assert.Null(stored.Pending);
        Assert.Equal(CurrentExistence.Exists, stored.CurrentExistence);
    }

    // A report naming a request the snapshot does not wait on is ignored, and still 204: the worker has nothing to retry.
    [Fact]
    public async Task AVmSnapshotReport_NamingAnotherRequest_Is204_AndChangesNothing()
    {
        Vm vm = await StoredVmWithSnapshotsWaitingAsync();
        VmSnapshot reverting = vm.Snapshots.Single(s => s.Name == "reverting");

        HttpResponseMessage response = await PatchAsync(
            $"/internal/vms/{vm.Id}/snapshots/{reverting.Id}/requests/{Guid.NewGuid()}", """{"outcome":"Done"}""");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(VmSnapshotRequestKind.Revert, (await ReloadSnapshotAsync(vm.Id, "reverting")).Pending!.Kind);
    }

    [Fact]
    public async Task AVmSnapshotObservation_InTheWorkersJson_WritesCurrent_AndLeavesItsDeleteWaiting()
    {
        Vm vm = await StoredVmWithSnapshotsWaitingAsync();
        VmSnapshot deleting = vm.Snapshots.Single(s => s.Name == "deleting");

        HttpResponseMessage response = await PatchAsync(
            $"/internal/vms/{vm.Id}/snapshots/{deleting.Id}/observation", """{"exists":false}""");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        VmSnapshot stored = await ReloadSnapshotAsync(vm.Id, "deleting");
        Assert.Equal(CurrentExistence.NotFound, stored.CurrentExistence);
        Assert.Equal(VmSnapshotRequestKind.Delete, stored.Pending!.Kind);
    }

    [Theory]
    [InlineData("requests/{0}", """{"outcome":"Maybe"}""")]
    [InlineData("observation", """{"exists":"yes"}""")]
    public async Task AVmSnapshotReportTheControlPlaneCannotRead_Is400_AndChangesNothing(string route, string json)
    {
        Vm vm = await StoredVmWithSnapshotsWaitingAsync();
        VmSnapshot reverting = vm.Snapshots.Single(s => s.Name == "reverting");

        HttpResponseMessage response = await PatchAsync(
            $"/internal/vms/{vm.Id}/snapshots/{reverting.Id}/{string.Format(route, reverting.Pending!.Id)}", json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        VmSnapshot stored = await ReloadSnapshotAsync(vm.Id, "reverting");
        Assert.Equal((CurrentExistence.Exists, VmSnapshotRequestKind.Revert), (stored.CurrentExistence, stored.Pending!.Kind));
    }

    // A create's Done must name the snapshot, or it would be left with nothing to revert or delete through.
    [Fact]
    public async Task AVmSnapshotCreateDone_WithoutTheSnapshotId_Is400_AndTheCreateKeepsWaiting()
    {
        Vm vm = await StoredVmWithSnapshotsWaitingAsync();
        VmSnapshot creating = vm.Snapshots.Single(s => s.Name == "creating");

        HttpResponseMessage response = await PatchAsync(
            $"/internal/vms/{vm.Id}/snapshots/{creating.Id}/requests/{creating.Pending!.Id}", """{"outcome":"Done"}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(VmSnapshotRequestKind.Create, (await ReloadSnapshotAsync(vm.Id, "creating")).Pending!.Kind);
    }

    [Theory]
    [InlineData("requests/{0}")]
    [InlineData("observation")]
    public async Task AVmSnapshotReport_ForAnotherVmsSnapshot_Is404(string route)
    {
        Vm vm = await StoredVmWithSnapshotsWaitingAsync();
        Vm other = await StoredVmWithSnapshotsWaitingAsync();
        VmSnapshot theirs = other.Snapshots.Single(s => s.Name == "reverting");

        HttpResponseMessage response = await PatchAsync(
            $"/internal/vms/{vm.Id}/snapshots/{theirs.Id}/{string.Format(route, theirs.Pending!.Id)}",
            route == "observation" ? """{"exists":false}""" : """{"outcome":"Done"}""");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        VmSnapshot stored = await ReloadSnapshotAsync(other.Id, "reverting");
        Assert.Equal((CurrentExistence.Exists, VmSnapshotRequestKind.Revert), (stored.CurrentExistence, stored.Pending!.Kind));
    }

    [Theory]
    [InlineData("requests/{0}", """{"outcome":"Done"}""")]
    [InlineData("observation", """{"exists":false}""")]
    public async Task EveryVmSnapshotRoute_WithoutTheToken_Is401_AndChangesNothing(string route, string json)
    {
        Vm vm = await StoredVmWithSnapshotsWaitingAsync();
        VmSnapshot deleting = vm.Snapshots.Single(s => s.Name == "deleting");

        HttpResponseMessage response = await PatchAsync(
            $"/internal/vms/{vm.Id}/snapshots/{deleting.Id}/{string.Format(route, deleting.Pending!.Id)}", json, token: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        VmSnapshot stored = await ReloadSnapshotAsync(vm.Id, "deleting");
        Assert.Equal((CurrentExistence.Exists, VmSnapshotRequestKind.Delete), (stored.CurrentExistence, stored.Pending!.Kind));
    }

    // What the control plane's environment sets on dev and prd.
    public sealed class CallbackHostFactory() : RealHostFactory(
        new Dictionary<string, string?> { ["Internal:Token"] = InternalToken }, new Dictionary<string, string>());
}
