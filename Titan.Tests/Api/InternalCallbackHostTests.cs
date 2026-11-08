using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Titan.Domain.Aggregates.LoadBalancer;
using Titan.Domain.Aggregates.Network;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Aggregates.VmTemplate;
using Titan.Domain.Aggregates.Volume;
using Titan.Domain.Values;
using Titan.Infrastructure.Persistence;
using Titan.Tests.Infrastructure.Incus;

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
        vm.Stop(Guid.NewGuid());
        using IServiceScope scope = factory.Services.CreateScope();
        TitanDbContext db = scope.ServiceProvider.GetRequiredService<TitanDbContext>();
        await db.Database.ExecuteSqlRawAsync(
            $"INSERT INTO core.vm_offerings (id, name, cpu_units, vcpu_count, memory_mb, price_per_hour) VALUES ('{Offering}', 'm', 1, 2, 4096, 311.256) ON CONFLICT (id) DO NOTHING");
        db.Vms.Add(vm);
        await db.SaveChangesAsync();
        return vm;
    }

    // Every command carries the id of its request, so the routes that matched a report to whatever waited are gone
    // (titan-ops architecture/resource-lifecycle.md#callback-routes): with the right token they are unknown routes.
    [Theory]
    [InlineData("/internal/vms/{0}/status")]
    [InlineData("/internal/vms/{0}/intents/{1}/failed")]
    [InlineData("/internal/vms/{0}/snapshots/{1}/status")]
    [InlineData("/internal/volumes/{0}/status")]
    [InlineData("/internal/volumes/{0}/snapshots/{1}/status")]
    [InlineData("/internal/networks/{0}/status")]
    [InlineData("/internal/networks/{0}/firewall-rules/{1}")]
    [InlineData("/internal/load-balancers/{0}/status")]
    [InlineData("/internal/templates/{0}/status")]
    public async Task AnOldReportRoute_IsNotFound(string route)
    {
        HttpResponseMessage response = await PatchAsync(string.Format(route, Guid.NewGuid(), Guid.NewGuid()), "{}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync());
    }

    // What makes the test above tell a missing route from a handler's 404: a live route answers an unknown resource with
    // an error body.
    [Fact]
    public async Task ALiveReportRoute_AnswersAnUnknownVm_WithAnErrorBody()
    {
        HttpResponseMessage response = await PatchAsync($"/internal/vms/{Guid.NewGuid()}/observation", """{"exists":false}""");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.NotEqual("", await response.Content.ReadAsStringAsync());
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
        Guid requestId = vm.Operation.PendingId!.Value;

        HttpResponseMessage response = await PatchAsync($"/internal/vms/{vm.Id}/requests/{requestId}",
            """{"outcome":"Done","observed":{"status":"Stopped","ipAddress":null}}""");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Vm stored = await ReloadAsync(vm.Id);
        Assert.Null(stored.Operation.PendingId);
        Assert.Equal(VmStatus.Stopped, stored.InstanceStatus);
        Assert.Equal(VmStatus.Stopped, stored.Status);
    }

    [Fact]
    public async Task AFailedWithItsReason_InTheWorkersJson_PutsTheStatusBackToTheLastIncusWord_AndKeepsTheReason()
    {
        Vm vm = await StoredVmWaitingOnAStopAsync();
        Guid requestId = vm.Operation.PendingId!.Value;

        HttpResponseMessage response = await PatchAsync($"/internal/vms/{vm.Id}/requests/{requestId}",
            """{"outcome":"Failed","reason":"The stop could not be completed.","detail":"incus: timeout"}""");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Vm stored = await ReloadAsync(vm.Id);
        Assert.Null(stored.Operation.PendingId);
        Assert.Equal(VmStatus.Running, stored.Status);
        Assert.Equal(("The stop could not be completed.", "incus: timeout"), (stored.Operation.FailureReason, stored.Operation.FailureDetail));
    }

    [Fact]
    public async Task AnObservation_InTheWorkersJson_WritesCurrent_AndAnswers204()
    {
        Vm vm = await StoredVmWaitingOnAStopAsync();

        HttpResponseMessage response = await PatchAsync($"/internal/vms/{vm.Id}/observation",
            """{"exists":true,"observed":{"status":"Frozen","ipAddress":"10.0.0.9","publicIpv6Address":"2001:db8::9"}}""");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Vm stored = await ReloadAsync(vm.Id);
        Assert.Equal((VmStatus.Frozen, "10.0.0.9", "2001:db8::9"), (stored.InstanceStatus, stored.IpAddress, stored.PublicIpv6Address));
        Assert.Equal(vm.Operation.PendingId, stored.Operation.PendingId);
        Assert.Equal(VmStatus.Stopping, stored.Status);
    }

    [Theory]
    [InlineData("""{"outcome":"Maybe"}""")]
    [InlineData("""{"outcome":"Done","observed":{"status":"Sleeping"}}""")]
    public async Task AReportTheControlPlaneCannotRead_Is400_WithItsCode_AndChangesNothing(string json)
    {
        Vm vm = await StoredVmWaitingOnAStopAsync();

        HttpResponseMessage response = await PatchAsync($"/internal/vms/{vm.Id}/requests/{vm.Operation.PendingId!.Value}", json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using JsonDocument envelope = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("INVALID_STATUS", envelope.RootElement.GetProperty("code").GetString());
        Assert.Equal(VmStatus.Stopping, (await ReloadAsync(vm.Id)).Status);
    }

    [Fact]
    public async Task ABodyThatIsNotTheReportsShape_Is400_AndChangesNothing()
    {
        Vm vm = await StoredVmWaitingOnAStopAsync();

        HttpResponseMessage response = await PatchAsync($"/internal/vms/{vm.Id}/observation", """{"exists":"yes"}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Vm stored = await ReloadAsync(vm.Id);
        Assert.Equal(VmStatus.Running, stored.InstanceStatus);
        Assert.Equal(VmStatus.Stopping, stored.Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("wrong-token")]
    public async Task WithoutTheToken_Is401_AndChangesNothing(string? token)
    {
        Vm vm = await StoredVmWaitingOnAStopAsync();

        HttpResponseMessage response = await PatchAsync($"/internal/vms/{vm.Id}/requests/{vm.Operation.PendingId!.Value}",
            """{"outcome":"Done","observed":{"status":"Stopped"}}""", token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(VmStatus.Stopping, (await ReloadAsync(vm.Id)).Status);
    }

    // ---------- the volume's and its snapshots' routes ----------

    private const string VolumeOffering = "vol-callback";

    private async Task<Volume> StoredVolumeWaitingOnADeleteAsync()
    {
        Volume volume = Volume.Create(TestTenant.WithRoomToSpare().Id, $"vol-{Guid.NewGuid():N}"[..14], "cluster-a", VolumeOffering, 10);
        volume.MarkCreated($"vol-{volume.Id:N}");
        VolumeSnapshot snapshot = volume.WithSnapshot("s1");
        volume.MarkSnapshotCreated(snapshot.Id, $"vol-{volume.Id:N}/s1");
        volume.DeleteSnapshot(snapshot.Id);
        volume.Delete();
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

        HttpResponseMessage response = await PatchAsync($"/internal/volumes/{volume.Id}/requests/{volume.Operation.PendingId!.Value}",
            """{"outcome":"Failed","observed":{"attachedTo":[]},"reason":"The volume could not be deleted.","detail":"incus: busy"}""");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Volume stored = await ReloadVolumeAsync(volume.Id);
        Assert.Null(stored.Operation.PendingId);
        Assert.Equal(VolumeStatus.Available, stored.Status);
        Assert.Equal("The volume could not be deleted.", stored.Operation.FailureReason);
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
        Assert.Equal(VolumeStatus.Deleting, stored.Status);
    }

    [Fact]
    public async Task ASnapshotDone_InTheWorkersJson_EndsItsDelete()
    {
        Volume volume = await StoredVolumeWaitingOnADeleteAsync();
        VolumeSnapshot snapshot = volume.Snapshots.Single();

        HttpResponseMessage response = await PatchAsync(
            $"/internal/volumes/{volume.Id}/snapshots/{snapshot.Id}/requests/{snapshot.Operation.PendingId!.Value}", """{"outcome":"Done"}""");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(VolumeSnapshotStatus.Deleted, (await ReloadVolumeAsync(volume.Id)).Snapshots.Single().Status);
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
        Assert.Equal(VolumeSnapshotStatus.Deleting, stored.Status);
        Assert.Null(stored.InstanceStatus);
    }

    [Theory]
    [InlineData("requests/{0}", """{"outcome":"Maybe"}""")]
    [InlineData("observation", """{"exists":"yes"}""")]
    public async Task AVolumeReportTheControlPlaneCannotRead_Is400_AndChangesNothing(string route, string json)
    {
        Volume volume = await StoredVolumeWaitingOnADeleteAsync();

        HttpResponseMessage response = await PatchAsync($"/internal/volumes/{volume.Id}/{string.Format(route, volume.Operation.PendingId!.Value)}", json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(VolumeStatus.Deleting, (await ReloadVolumeAsync(volume.Id)).Status);
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
            $"/internal/volumes/{volume.Id}/{string.Format(route, volume.Operation.PendingId!.Value, snapshot.Id, snapshot.Operation.PendingId!.Value)}", json, token: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Volume stored = await ReloadVolumeAsync(volume.Id);
        Assert.Equal((VolumeStatus.Deleting, (VolumeStatus?)VolumeStatus.Available), (stored.Status, stored.InstanceStatus));
        VolumeSnapshot storedSnapshot = stored.Snapshots.Single();
        Assert.Equal((VolumeSnapshotStatus.Deleting, (VolumeSnapshotStatus?)VolumeSnapshotStatus.Available),
            (storedSnapshot.Status, storedSnapshot.InstanceStatus));
    }

    // ---------- the network's and its firewall rules' routes ----------

    private async Task<Network> StoredNetworkAndRuleWaitingOnTheirDeletesAsync()
    {
        Network network = Network.Create(TestTenant.WithRoomToSpare().Id, NetworkName.Parse($"net-{Guid.NewGuid():N}"[..14]),
            "cluster-a", null, wantsPublicIpv6: false);
        network.ReportDone(network.Operation.PendingId!.Value, $"net-{network.Id:N}", NetworkStatus.Created, "10.0.0.0/24", "10.0.0.1");
        FirewallRule rule = network.WithFirewallRule(FirewallDirection.Ingress, FirewallProtocol.TCP, null, FirewallCidr.Parse("10.0.0.0/8"));
        network.ReportRuleDone(rule.Id, rule.Operation.PendingId!.Value, $"acl-{network.Id:N}/1", FirewallRuleStatus.enabled);
        network.DeleteFirewallRule(rule.Id);
        network.Delete();
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

        HttpResponseMessage response = await PatchAsync($"/internal/networks/{network.Id}/requests/{network.Operation.PendingId!.Value}",
            """{"outcome":"Failed","observed":{"status":"Created"},"reason":"The network could not be deleted.","detail":"incus: in use"}""");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Network stored = await ReloadNetworkAsync(network.Id);
        Assert.Null(stored.Operation.PendingId);
        Assert.False(stored.IsDeletedOrDeleting);
        Assert.Equal(("The network could not be deleted.", "incus: in use"), (stored.Operation.FailureReason, stored.Operation.FailureDetail));
    }

    [Fact]
    public async Task ANetworkDone_InTheWorkersJson_EndsItsDelete_AndTheRulesWithIt()
    {
        Network network = await StoredNetworkAndRuleWaitingOnTheirDeletesAsync();

        HttpResponseMessage response = await PatchAsync($"/internal/networks/{network.Id}/requests/{network.Operation.PendingId!.Value}",
            """{"outcome":"Done"}""");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Network stored = await ReloadNetworkAsync(network.Id);
        Assert.Null(stored.Operation.PendingId);
        Assert.Equal(NetworkStatus.Deleted, stored.Status);
        Assert.Equal(FirewallRuleStatus.Deleted, stored.FirewallRules.Single().Status);
    }

    [Fact]
    public async Task ANetworkObservation_InTheWorkersJson_WritesCurrent_AndLeavesTheDeleteWaiting()
    {
        Network network = await StoredNetworkAndRuleWaitingOnTheirDeletesAsync();

        HttpResponseMessage response = await PatchAsync($"/internal/networks/{network.Id}/observation",
            """{"exists":true,"observed":{"status":"Errored"}}""");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Network stored = await ReloadNetworkAsync(network.Id);
        Assert.Equal(NetworkStatus.Errored, stored.InstanceStatus);
        Assert.Equal(NetworkStatus.Deleting, stored.Status);
    }

    [Fact]
    public async Task ARuleDone_InTheWorkersJson_EndsItsDelete()
    {
        Network network = await StoredNetworkAndRuleWaitingOnTheirDeletesAsync();
        FirewallRule rule = network.FirewallRules.Single();

        HttpResponseMessage response = await PatchAsync(
            $"/internal/networks/{network.Id}/firewall-rules/{rule.Id}/requests/{rule.Operation.PendingId!.Value}", """{"outcome":"Done"}""");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Network stored = await ReloadNetworkAsync(network.Id);
        Assert.Equal(FirewallRuleStatus.Deleted, stored.FirewallRules.Single().Status);
        Assert.Equal(NetworkStatus.Deleting, stored.Status);
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
        Assert.Equal(FirewallRuleStatus.disabled, stored.InstanceStatus);
        Assert.Equal(FirewallRuleStatus.Deleting, stored.Status);
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
            $"/internal/networks/{network.Id}/{string.Format(route, network.Operation.PendingId!.Value, rule.Id, rule.Operation.PendingId!.Value)}", json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Network stored = await ReloadNetworkAsync(network.Id);
        Assert.Equal((NetworkStatus.Created, NetworkStatus.Deleting), (stored.InstanceStatus!.Value, stored.Status));
        Assert.Equal((FirewallRuleStatus.enabled, FirewallRuleStatus.Deleting),
            (stored.FirewallRules.Single().InstanceStatus!.Value, stored.FirewallRules.Single().Status));
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
            $"/internal/networks/{network.Id}/{string.Format(route, network.Operation.PendingId!.Value, rule.Id, rule.Operation.PendingId!.Value)}", json, token: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Network stored = await ReloadNetworkAsync(network.Id);
        Assert.Equal(NetworkStatus.Deleting, stored.Status);
        Assert.Equal(FirewallRuleStatus.Deleting, stored.FirewallRules.Single().Status);
    }

    // ---------- the VM's snapshots' routes ----------

    // A running VM with a snapshot waiting on its create, and two Incus holds: one waiting on its revert, one on its delete.
    // The VM takes one create or revert at a time (it is Snapshotting for it), so the revert is put on the snapshot
    // alone: the VM would refuse the creates that follow while a revert runs.
    private async Task<Vm> StoredVmWithSnapshotsWaitingAsync()
    {
        Vm vm = Vm.Create(TestTenant.WithRoomToSpare().Id, Guid.NewGuid(), $"vm-{Guid.NewGuid():N}"[..14], Offering, "tpl", "cluster-a", 20);
        vm.MarkRunning($"ext-{vm.Id:N}", "10.0.0.5");
        VmSnapshot reverting = vm.WithSnapshot("reverting");
        vm.SnapshotCreateSucceeded(reverting.Id, reverting.Operation.PendingId!.Value, $"ext-{vm.Id:N}/s1");
        reverting.Revert(Guid.NewGuid());
        VmSnapshot deleting = vm.WithSnapshot("deleting");
        vm.SnapshotCreateSucceeded(deleting.Id, deleting.Operation.PendingId!.Value, $"ext-{vm.Id:N}/s2");
        vm.DeleteSnapshot(deleting.Id, Guid.NewGuid());
        vm.AddSnapshot(Guid.NewGuid(), Guid.NewGuid(), "creating");
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
            $"/internal/vms/{vm.Id}/snapshots/{deleting.Id}/requests/{deleting.Operation.PendingId!.Value}",
            """{"outcome":"Failed","reason":"The snapshot could not be deleted.","detail":"incus: busy"}""");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        VmSnapshot stored = await ReloadSnapshotAsync(vm.Id, "deleting");
        Assert.Null(stored.Operation.PendingId);
        Assert.Equal(VmSnapshotStatus.Available, stored.Status);
        Assert.Equal("The snapshot could not be deleted.", stored.Operation.FailureReason);
    }

    [Fact]
    public async Task AVmSnapshotDone_InTheWorkersJson_EndsItsRevert()
    {
        Vm vm = await StoredVmWithSnapshotsWaitingAsync();
        VmSnapshot reverting = vm.Snapshots.Single(s => s.Name == "reverting");

        HttpResponseMessage response = await PatchAsync(
            $"/internal/vms/{vm.Id}/snapshots/{reverting.Id}/requests/{reverting.Operation.PendingId!.Value}", """{"outcome":"Done"}""");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        VmSnapshot stored = await ReloadSnapshotAsync(vm.Id, "reverting");
        Assert.Null(stored.Operation.PendingId);
        Assert.Equal(VmSnapshotStatus.Available, stored.Status);
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
        Assert.Equal(VmSnapshotStatus.Reverting, (await ReloadSnapshotAsync(vm.Id, "reverting")).Status);
    }

    [Fact]
    public async Task AVmSnapshotObservation_InTheWorkersJson_RemembersIncusLostIt_AndLeavesItsDeleteWaiting()
    {
        Vm vm = await StoredVmWithSnapshotsWaitingAsync();
        VmSnapshot deleting = vm.Snapshots.Single(s => s.Name == "deleting");

        HttpResponseMessage response = await PatchAsync(
            $"/internal/vms/{vm.Id}/snapshots/{deleting.Id}/observation", """{"exists":false}""");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        VmSnapshot stored = await ReloadSnapshotAsync(vm.Id, "deleting");
        Assert.Equal(VmSnapshotStatus.Deleting, stored.Status);
        Assert.Null(stored.InstanceStatus);
    }

    [Theory]
    [InlineData("requests/{0}", """{"outcome":"Maybe"}""")]
    [InlineData("observation", """{"exists":"yes"}""")]
    public async Task AVmSnapshotReportTheControlPlaneCannotRead_Is400_AndChangesNothing(string route, string json)
    {
        Vm vm = await StoredVmWithSnapshotsWaitingAsync();
        VmSnapshot reverting = vm.Snapshots.Single(s => s.Name == "reverting");

        HttpResponseMessage response = await PatchAsync(
            $"/internal/vms/{vm.Id}/snapshots/{reverting.Id}/{string.Format(route, reverting.Operation.PendingId!.Value)}", json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        VmSnapshot stored = await ReloadSnapshotAsync(vm.Id, "reverting");
        Assert.Equal((VmSnapshotStatus.Reverting, (VmSnapshotStatus?)VmSnapshotStatus.Available), (stored.Status, stored.InstanceStatus));
    }

    // A create's Done must name the snapshot, or it would be left with nothing to revert or delete through.
    [Fact]
    public async Task AVmSnapshotCreateDone_WithoutTheSnapshotId_Is422_AndTheCreateKeepsWaiting()
    {
        Vm vm = await StoredVmWithSnapshotsWaitingAsync();
        VmSnapshot creating = vm.Snapshots.Single(s => s.Name == "creating");

        HttpResponseMessage response = await PatchAsync(
            $"/internal/vms/{vm.Id}/snapshots/{creating.Id}/requests/{creating.Operation.PendingId!.Value}", """{"outcome":"Done"}""");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(VmSnapshotStatus.Creating, (await ReloadSnapshotAsync(vm.Id, "creating")).Status);
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
            $"/internal/vms/{vm.Id}/snapshots/{theirs.Id}/{string.Format(route, theirs.Operation.PendingId!.Value)}",
            route == "observation" ? """{"exists":false}""" : """{"outcome":"Done"}""");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        VmSnapshot stored = await ReloadSnapshotAsync(other.Id, "reverting");
        Assert.Equal((VmSnapshotStatus.Reverting, (VmSnapshotStatus?)VmSnapshotStatus.Available), (stored.Status, stored.InstanceStatus));
    }

    [Theory]
    [InlineData("requests/{0}", """{"outcome":"Done"}""")]
    [InlineData("observation", """{"exists":false}""")]
    public async Task EveryVmSnapshotRoute_WithoutTheToken_Is401_AndChangesNothing(string route, string json)
    {
        Vm vm = await StoredVmWithSnapshotsWaitingAsync();
        VmSnapshot deleting = vm.Snapshots.Single(s => s.Name == "deleting");

        HttpResponseMessage response = await PatchAsync(
            $"/internal/vms/{vm.Id}/snapshots/{deleting.Id}/{string.Format(route, deleting.Operation.PendingId!.Value)}", json, token: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        VmSnapshot stored = await ReloadSnapshotAsync(vm.Id, "deleting");
        Assert.Equal((VmSnapshotStatus.Deleting, (VmSnapshotStatus?)VmSnapshotStatus.Available), (stored.Status, stored.InstanceStatus));
    }

    // ---------- the load balancer's routes ----------

    private const string LoadBalancerSubnet = "2001:db8:0:3::/64";

    // A load balancer waiting on its create (Creating), or one Incus holds that waits on its delete (Deleting).
    private async Task<LoadBalancer> StoredLoadBalancerAsync(bool deleting)
    {
        Guid tenantId = TestTenant.WithRoomToSpare().Id;
        Guid networkId = Guid.NewGuid();
        Vm backend = Vm.Create(tenantId, networkId, $"vm-{Guid.NewGuid():N}"[..14], Offering, "tpl", "cluster-a", 20);
        backend.MarkRunning($"ext-{backend.Id:N}", "10.0.0.5");
        LoadBalancer lb = LoadBalancer.Create(tenantId, $"lb-{Guid.NewGuid():N}"[..12], networkId, "net-x", LoadBalancerSubnet,
            "cluster-a", LoadBalancerVip.For(LoadBalancerSubnet, 1), [new LoadBalancerListenerSpec(LoadBalancerProtocol.TCP, 80, 8080)],
            [backend]);
        if (deleting)
        {
            lb.CreateSucceeded(lb.Operation.PendingId!.Value, lb.Vip);
            lb.Delete();
        }
        using IServiceScope scope = factory.Services.CreateScope();
        TitanDbContext db = scope.ServiceProvider.GetRequiredService<TitanDbContext>();
        db.LoadBalancers.Add(lb);
        await db.SaveChangesAsync();
        return lb;
    }

    private async Task<LoadBalancer> ReloadLoadBalancerAsync(Guid id)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        TitanDbContext db = scope.ServiceProvider.GetRequiredService<TitanDbContext>();
        return await db.LoadBalancers.AsNoTracking().Include(lb => lb.Listeners).SingleAsync(lb => lb.Id == id);
    }

    [Fact]
    public async Task ALoadBalancerCreateDone_InTheWorkersJson_RecordsTheVip_AndAppliesTheRows()
    {
        LoadBalancer lb = await StoredLoadBalancerAsync(deleting: false);

        HttpResponseMessage response = await PatchAsync(
            $"/internal/load-balancers/{lb.Id}/requests/{lb.Operation.PendingId!.Value}", $$"""{"outcome":"Done","externalId":"{{lb.Vip}}"}""");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        LoadBalancer stored = await ReloadLoadBalancerAsync(lb.Id);
        Assert.Null(stored.Operation.PendingId);
        Assert.Equal((LoadBalancerStatus.Available, lb.Vip), (stored.Status, stored.ExternalId));
        Assert.All(stored.Listeners, l => Assert.True(l.IsApplied));
    }

    [Fact]
    public async Task ALoadBalancerDeleteFailed_InTheWorkersJson_GivesItBack_WithItsReason()
    {
        LoadBalancer lb = await StoredLoadBalancerAsync(deleting: true);

        HttpResponseMessage response = await PatchAsync(
            $"/internal/load-balancers/{lb.Id}/requests/{lb.Operation.PendingId!.Value}",
            """{"outcome":"Failed","reason":"The load balancer could not be deleted.","detail":"incus: busy"}""");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        LoadBalancer stored = await ReloadLoadBalancerAsync(lb.Id);
        Assert.Null(stored.Operation.PendingId);
        Assert.Equal(LoadBalancerStatus.Available, stored.Status);
        Assert.Equal("The load balancer could not be deleted.", stored.Operation.FailureReason);
    }

    // A report naming a request the load balancer does not wait on is ignored, and still 204: the worker has nothing to retry.
    [Fact]
    public async Task ALoadBalancerReport_NamingAnotherRequest_Is204_AndChangesNothing()
    {
        LoadBalancer lb = await StoredLoadBalancerAsync(deleting: true);

        HttpResponseMessage response = await PatchAsync(
            $"/internal/load-balancers/{lb.Id}/requests/{Guid.NewGuid()}", """{"outcome":"Done"}""");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(LoadBalancerStatus.Deleting, (await ReloadLoadBalancerAsync(lb.Id)).Status);
    }

    [Fact]
    public async Task ALoadBalancerObservation_InTheWorkersJson_WritesWhatWasSeen_AndLeavesItsDeleteWaiting()
    {
        LoadBalancer lb = await StoredLoadBalancerAsync(deleting: true);

        HttpResponseMessage response = await PatchAsync($"/internal/load-balancers/{lb.Id}/observation", """{"exists":false}""");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        LoadBalancer stored = await ReloadLoadBalancerAsync(lb.Id);
        Assert.Equal((LoadBalancerStatus.Deleting, (LoadBalancerStatus?)null), (stored.Status, stored.InstanceStatus));
    }

    [Theory]
    [InlineData("requests/{0}", """{"outcome":"Maybe"}""")]
    [InlineData("requests/{0}", """{"reason":"no outcome"}""")]
    [InlineData("observation", """{"exists":"yes"}""")]
    public async Task ALoadBalancerReportTheControlPlaneCannotRead_Is400_AndChangesNothing(string route, string json)
    {
        LoadBalancer lb = await StoredLoadBalancerAsync(deleting: true);

        HttpResponseMessage response = await PatchAsync(
            $"/internal/load-balancers/{lb.Id}/{string.Format(route, lb.Operation.PendingId!.Value)}", json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        LoadBalancer stored = await ReloadLoadBalancerAsync(lb.Id);
        Assert.Equal((LoadBalancerStatus.Deleting, (LoadBalancerStatus?)LoadBalancerStatus.Available), (stored.Status, stored.InstanceStatus));
    }

    // A create's Done must name the VIP, or the load balancer would be left with nothing to delete through.
    [Fact]
    public async Task ALoadBalancerCreateDone_WithoutTheVip_Is422_AndTheCreateKeepsWaiting()
    {
        LoadBalancer lb = await StoredLoadBalancerAsync(deleting: false);

        HttpResponseMessage response = await PatchAsync(
            $"/internal/load-balancers/{lb.Id}/requests/{lb.Operation.PendingId!.Value}", """{"outcome":"Done"}""");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(LoadBalancerStatus.Creating, (await ReloadLoadBalancerAsync(lb.Id)).Status);
    }

    [Theory]
    [InlineData("requests/{0}", """{"outcome":"Done"}""")]
    [InlineData("observation", """{"exists":false}""")]
    public async Task ALoadBalancerReport_ForNoLoadBalancer_Is404(string route, string json)
    {
        HttpResponseMessage response = await PatchAsync(
            $"/internal/load-balancers/{Guid.NewGuid()}/{string.Format(route, Guid.NewGuid())}", json);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("requests/{0}", """{"outcome":"Done"}""")]
    [InlineData("observation", """{"exists":false}""")]
    public async Task EveryLoadBalancerRoute_WithoutTheToken_Is401_AndChangesNothing(string route, string json)
    {
        LoadBalancer lb = await StoredLoadBalancerAsync(deleting: true);

        HttpResponseMessage response = await PatchAsync(
            $"/internal/load-balancers/{lb.Id}/{string.Format(route, lb.Operation.PendingId!.Value)}", json, token: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        LoadBalancer stored = await ReloadLoadBalancerAsync(lb.Id);
        Assert.Equal((LoadBalancerStatus.Deleting, (LoadBalancerStatus?)LoadBalancerStatus.Available), (stored.Status, stored.InstanceStatus));
    }

    // ---------- a template's routes, under its cluster ----------

    private const string TemplateCluster = "cluster-a";
    private const string TemplateAlias = "tpl-callback";

    // A template waiting on its sync in the configured cluster, or one Incus holds there with a re-sync waiting.
    private async Task<VmTemplate> StoredTemplateWaitingOnASyncAsync(bool resync = false)
    {
        VmTemplate template = VmTemplate.Create($"tpl-{Guid.NewGuid():N}"[..14], null, "ubuntu/22.04", 10, Offering);
        if (resync)
        {
            template.SyncSucceededIn(TemplateCluster, template.SyncIn(TemplateCluster), TemplateAlias);
            template.MarkNotFoundIn(TemplateCluster);
        }
        template.SyncIn(TemplateCluster);
        using IServiceScope scope = factory.Services.CreateScope();
        TitanDbContext db = scope.ServiceProvider.GetRequiredService<TitanDbContext>();
        await db.Database.ExecuteSqlRawAsync(
            $"INSERT INTO core.vm_offerings (id, name, cpu_units, vcpu_count, memory_mb, price_per_hour) VALUES ('{Offering}', 'm', 1, 2, 4096, 311.256) ON CONFLICT (id) DO NOTHING");
        db.VmTemplates.Add(template);
        await db.SaveChangesAsync();
        return template;
    }

    private async Task<VmTemplateCluster> ReloadTemplateClusterAsync(Guid templateId)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        TitanDbContext db = scope.ServiceProvider.GetRequiredService<TitanDbContext>();
        VmTemplate template = await db.VmTemplates.AsNoTracking().Include(t => t.Clusters).SingleAsync(t => t.Id == templateId);
        return template.Clusters.Single(c => c.ClusterId == TemplateCluster);
    }

    private static string TemplateRoute(VmTemplate template, string route) =>
        $"/internal/templates/{template.Id}/clusters/{TemplateCluster}/"
        + string.Format(route, template.Clusters.Single(c => c.ClusterId == TemplateCluster).Operation.PendingId!.Value);

    [Fact]
    public async Task ATemplateSyncDone_InTheWorkersJson_RecordsTheAlias_AndAnswers204()
    {
        VmTemplate template = await StoredTemplateWaitingOnASyncAsync();

        HttpResponseMessage response = await PatchAsync(TemplateRoute(template, "requests/{0}"),
            $$"""{"outcome":"Done","externalId":"{{TemplateAlias}}"}""");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        VmTemplateCluster stored = await ReloadTemplateClusterAsync(template.Id);
        Assert.Null(stored.Operation.PendingId);
        Assert.Equal((VmTemplateClusterStatus.Available, TemplateAlias), (stored.Status, stored.ExternalTemplateId));
    }

    [Fact]
    public async Task ATemplateSyncFailed_InTheWorkersJson_RecordsTheReason_AndAnswers204()
    {
        VmTemplate template = await StoredTemplateWaitingOnASyncAsync();

        HttpResponseMessage response = await PatchAsync(TemplateRoute(template, "requests/{0}"),
            """{"outcome":"Failed","reason":"Template sync failed due to a system error.","detail":"incus: timeout"}""");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        VmTemplateCluster stored = await ReloadTemplateClusterAsync(template.Id);
        Assert.Null(stored.Operation.PendingId);
        Assert.Equal(VmTemplateClusterStatus.Failed, stored.Status);
        Assert.Equal(("Template sync failed due to a system error.", "incus: timeout"), (stored.Operation.FailureReason, stored.Operation.FailureDetail));
    }

    // A report naming a request the cluster does not wait on is ignored, and still 204: the worker has nothing to retry.
    [Fact]
    public async Task ATemplateReport_NamingAnotherRequest_Is204_AndChangesNothing()
    {
        VmTemplate template = await StoredTemplateWaitingOnASyncAsync();

        HttpResponseMessage response = await PatchAsync(
            $"/internal/templates/{template.Id}/clusters/{TemplateCluster}/requests/{Guid.NewGuid()}",
            $$"""{"outcome":"Done","externalId":"{{TemplateAlias}}"}""");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(VmTemplateClusterStatus.Syncing, (await ReloadTemplateClusterAsync(template.Id)).Status);
    }

    [Fact]
    public async Task ATemplateObservation_InTheWorkersJson_IsSeenOnTheSync_AndLeavesItWaiting()
    {
        VmTemplate template = await StoredTemplateWaitingOnASyncAsync(resync: true);

        HttpResponseMessage response = await PatchAsync(TemplateRoute(template, "observation"), """{"exists":true}""");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        VmTemplateCluster stored = await ReloadTemplateClusterAsync(template.Id);
        Assert.Equal((VmTemplateClusterStatus.Syncing, VmTemplateClusterStatus.Available), (stored.Status, stored.InstanceStatus));
        Assert.NotNull(stored.Operation.PendingId);
    }

    [Theory]
    [InlineData("requests/{0}", """{"outcome":"Maybe","externalId":"tpl-callback"}""")]
    [InlineData("requests/{0}", """{"reason":"no outcome"}""")]
    [InlineData("observation", """{"exists":"yes"}""")]
    public async Task ATemplateReportTheControlPlaneCannotRead_Is400_AndChangesNothing(string route, string json)
    {
        VmTemplate template = await StoredTemplateWaitingOnASyncAsync();

        HttpResponseMessage response = await PatchAsync(TemplateRoute(template, route), json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        VmTemplateCluster stored = await ReloadTemplateClusterAsync(template.Id);
        Assert.Equal(VmTemplateClusterStatus.Syncing, stored.Status);
        Assert.NotNull(stored.Operation.PendingId);
    }

    // A sync's Done must name the alias while the cluster has none: a VM is created from it.
    [Fact]
    public async Task ATemplateSyncDone_WithoutTheAlias_Is422_AndTheSyncKeepsWaiting()
    {
        VmTemplate template = await StoredTemplateWaitingOnASyncAsync();

        HttpResponseMessage response = await PatchAsync(TemplateRoute(template, "requests/{0}"), """{"outcome":"Done"}""");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        VmTemplateCluster stored = await ReloadTemplateClusterAsync(template.Id);
        Assert.Equal(VmTemplateClusterStatus.Syncing, stored.Status);
        Assert.NotNull(stored.Operation.PendingId);
    }

    [Theory]
    [InlineData("requests/{0}", """{"outcome":"Done","externalId":"tpl-callback"}""")]
    [InlineData("observation", """{"exists":false}""")]
    public async Task ATemplateReport_ForNoTemplate_Is404_WithItsCode(string route, string json)
    {
        HttpResponseMessage response = await PatchAsync(
            $"/internal/templates/{Guid.NewGuid()}/clusters/{TemplateCluster}/{string.Format(route, Guid.NewGuid())}", json);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using JsonDocument envelope = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("TEMPLATE_NOT_FOUND", envelope.RootElement.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("requests/{0}", """{"outcome":"Done","externalId":"tpl-callback"}""")]
    [InlineData("observation", """{"exists":false}""")]
    public async Task ATemplateReport_ForAClusterNotConfigured_Is404_AndChangesNothing(string route, string json)
    {
        VmTemplate template = await StoredTemplateWaitingOnASyncAsync();
        Guid requestId = template.Clusters.Single().Operation.PendingId!.Value;

        HttpResponseMessage response = await PatchAsync(
            $"/internal/templates/{template.Id}/clusters/vn-hcm-1/{string.Format(route, requestId)}", json);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using JsonDocument envelope = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("CLUSTER_NOT_FOUND", envelope.RootElement.GetProperty("code").GetString());
        Assert.Equal(VmTemplateClusterStatus.Syncing, (await ReloadTemplateClusterAsync(template.Id)).Status);
    }

    [Theory]
    [InlineData("requests/{0}", """{"outcome":"Done","externalId":"tpl-callback"}""")]
    [InlineData("observation", """{"exists":false}""")]
    public async Task EveryTemplateRoute_WithoutTheToken_Is401_AndChangesNothing(string route, string json)
    {
        VmTemplate template = await StoredTemplateWaitingOnASyncAsync();

        HttpResponseMessage response = await PatchAsync(TemplateRoute(template, route), json, token: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        VmTemplateCluster stored = await ReloadTemplateClusterAsync(template.Id);
        Assert.Equal(VmTemplateClusterStatus.Syncing, stored.Status);
        Assert.NotNull(stored.Operation.PendingId);
    }

    // What the control plane's environment sets on dev and prd, and one cluster for the template routes, which refuse a
    // cluster the configuration does not name. A cluster needs Titan's TLS identity to start.
    public sealed class CallbackHostFactory() : RealHostFactory(
        new Dictionary<string, string?> { ["Internal:Token"] = InternalToken }, OneCluster());

    private static Dictionary<string, string> OneCluster()
    {
        var identity = IncusTestKit.NewIdentity();
        return new Dictionary<string, string>
        {
            ["Clusters:0:Id"] = TemplateCluster,
            ["Clusters:0:Name"] = "Cluster A",
            ["Clusters:0:Endpoint"] = "https://localhost:8443",
            ["Clusters:0:ServerFingerprint"] = "ffcf7803b2371e90834c2dd2a454f48bcb9c541b16dd7dc54a93a104b1aa108f",
            ["ProviderClientIdentity:CertificatePem"] = identity.CertificatePem,
            ["ProviderClientIdentity:KeyPem"] = identity.KeyPem,
        };
    }
}
