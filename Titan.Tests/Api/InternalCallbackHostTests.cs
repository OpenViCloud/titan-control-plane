using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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

    // What the control plane's environment sets on dev and prd.
    public sealed class CallbackHostFactory() : RealHostFactory(
        new Dictionary<string, string?> { ["Internal:Token"] = InternalToken }, new Dictionary<string, string>());
}
