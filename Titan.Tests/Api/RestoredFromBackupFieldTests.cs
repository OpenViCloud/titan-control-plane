using System.Text.Json;
using Moq;
using Titan.Application.Abstractions;
using Titan.Application.Queries.Compute.Vm;
using Titan.Application.Queries.Compute.Vm.Contracts;
using Titan.Application.Queries.Compute.Volume;
using Titan.Application.Queries.Compute.Volume.Contracts;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Aggregates.Volume;
using Titan.Domain.Repositories;

namespace Titan.Tests.Api;

// A tenant sees which of its VMs and volumes an admin restored from a backup: they are the ones that do not count toward
// its quota, so a client that counts usage from the lists needs the field
// (titan-ops features/backup/design.md#restore-as-new-resource). Options are FastEndpoints' default (web defaults).
public class RestoredFromBackupFieldTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly Guid Tenant = Guid.NewGuid();

    private static JsonElement Json<T>(T value) =>
        JsonDocument.Parse(JsonSerializer.Serialize(value, Web)).RootElement;

    private static Vm CreatedVm()
    {
        Vm vm = Vm.Create(Tenant, Guid.NewGuid(), "web-01", "offering-1", "tmpl-1", "cluster-1", 0);
        vm.MarkRunning("vm-web-01", "10.0.0.2");
        return vm;
    }

    private static Volume CreatedVolume()
    {
        Volume volume = Volume.Create(Tenant, "data-01", "cluster-1", "standard", 10);
        volume.MarkCreated("vol-data-01");
        return volume;
    }

    private static ICurrentUser AMemberOfTheTenant()
    {
        var user = new Mock<ICurrentUser>();
        user.Setup(u => u.TenantId).Returns(Tenant);
        return user.Object;
    }

    [Fact]
    public void Vm_RestoredFromBackup_IsOnTheWireUnderThatName_TrueOnlyForARestoredOne()
    {
        Vm created = CreatedVm();
        Vm restored = Vm.RestoreFromBackup(created, "web-01-restored");

        Assert.True(Json(VmQueryResult.From(restored)).GetProperty("restoredFromBackup").GetBoolean());
        Assert.False(Json(VmQueryResult.From(created)).GetProperty("restoredFromBackup").GetBoolean());
    }

    [Fact]
    public void Volume_RestoredFromBackup_IsOnTheWireUnderThatName_TrueOnlyForARestoredOne()
    {
        Volume created = CreatedVolume();
        Volume restored = Volume.RestoreFromBackup(created, "data-01");

        Assert.True(Json(VolumeQueryResult.From(restored)).GetProperty("restoredFromBackup").GetBoolean());
        Assert.False(Json(VolumeQueryResult.From(created)).GetProperty("restoredFromBackup").GetBoolean());
    }

    [Fact]
    public async Task TheTenantsVmListAndDetail_SayWhichVmWasRestored()
    {
        Vm created = CreatedVm();
        Vm restored = Vm.RestoreFromBackup(created, "web-01-restored");
        var repo = new Mock<IVmRepository>();
        repo.Setup(r => r.FindByIdAsync(restored.Id, default)).ReturnsAsync(restored);
        repo.Setup(r => r.FindByIdAsync(created.Id, default)).ReturnsAsync(created);
        repo.Setup(r => r.ListByTenantAsync(Tenant, default)).ReturnsAsync([created, restored]);
        var get = new GetVmQueryHandler(repo.Object, AMemberOfTheTenant());

        List<VmQueryResult> all = await new ListVmsQueryHandler(repo.Object, AMemberOfTheTenant()).HandleAsync(new ListVmsQuery());

        Assert.True((await get.HandleAsync(new GetVmQuery(restored.Id))).RestoredFromBackup);
        Assert.False((await get.HandleAsync(new GetVmQuery(created.Id))).RestoredFromBackup);
        Assert.Equal(
            new[] { (created.Id, false), (restored.Id, true) },
            all.Select(v => (v.Id, v.RestoredFromBackup)).ToArray());
    }

    [Fact]
    public async Task TheTenantsVolumeList_SaysWhichVolumeWasRestored()
    {
        Volume created = CreatedVolume();
        Volume restored = Volume.RestoreFromBackup(created, "data-01");
        var repo = new Mock<IVolumeRepository>();
        repo.Setup(r => r.ListByTenantAsync(Tenant, default)).ReturnsAsync([created, restored]);

        List<VolumeQueryResult> all = await new ListVolumesQueryHandler(repo.Object, AMemberOfTheTenant()).HandleAsync(new ListVolumesQuery());

        Assert.Equal(
            new[] { (created.Id, false), (restored.Id, true) },
            all.Select(v => (v.Id, v.RestoredFromBackup)).ToArray());
    }
}
