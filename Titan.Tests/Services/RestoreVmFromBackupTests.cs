using System.Text.Json;
using Titan.Application.Services.Platform.Tenant;
using Titan.Domain.Aggregates.AdminAuditLog;
using Titan.Domain.Aggregates.BackupRecord;
using Titan.Domain.Aggregates.ProviderCommand;
using Titan.Domain.Aggregates.Tenant;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Aggregates.Volume;
using Titan.Domain.Exceptions;

namespace Titan.Tests.Services;

// A platform admin restores one backup archive of a VM as a new VM of the same tenant, next to the original, with a
// copy of every volume Incus holds attached to it (titan-ops features/backup/design.md#restore-as-new-resource). A VM's
// archive holds its root disk only, so each volume comes from its own archive, the one taken closest to the VM's. The
// rows never count toward the tenant's quota and are never billed; the volumes' commands go out before the VM's.
public class RestoreVmFromBackupTests
{
    private readonly RestoreFromBackupWorld _world = new();

    private string VmKey => RestoreFromBackupWorld.KeyOf(_world.VmArchiveTakenAt);

    private async Task<Vm> AVmWithAnArchiveAsync(string name = "web-1")
    {
        Vm source = await _world.ARunningVmAsync(name);
        await _world.AVmArchiveAsync(source);
        return source;
    }

    private static JsonElement PayloadOf(ProviderCommand command) => JsonDocument.Parse(command.Payload).RootElement;

    // ---- A VM with no volume.

    [Fact]
    public async Task ARestore_MakesANewFreeVmOfTheSameTenant_BuiltLikeTheOriginal_WaitingOnItsCreate()
    {
        Vm source = await AVmWithAnArchiveAsync();

        Guid newId = await _world.RestoreVmAsync(source.Id, VmKey, "web-1-restored");

        List<Vm> vms = await _world.VmsAsync();
        Assert.Equal(2, vms.Count);
        Vm restored = vms.Single(v => v.Id == newId);
        Assert.NotEqual(source.Id, restored.Id);
        Assert.True(restored.RestoredFromBackup);
        Assert.Equal("web-1-restored", restored.Name);
        Assert.Equal(
            (_world.Owner.Id, source.NetworkId, source.VmOfferingId, source.TemplateId, source.ClusterId, source.DiskSizeGb),
            (restored.TenantId, restored.NetworkId, restored.VmOfferingId, restored.TemplateId, restored.ClusterId, restored.DiskSizeGb));
        Assert.Equal((600, "#cloud-config\n"), (restored.StopTimeoutSeconds, restored.UserData));
        Assert.Equal(VmStatus.Creating, restored.Status);
        Assert.NotNull(restored.Operation.PendingId);
        Assert.Null(restored.ExternalId);
        Assert.Equal((false, false), (restored.OccupiesQuota, restored.IsBilled));
        // The original is as it was, and no volume was made.
        Vm original = vms.Single(v => v.Id == source.Id);
        Assert.Equal((VmStatus.Running, "web-1", false), (original.Status, original.Name, original.RestoredFromBackup));
        Assert.Empty(await _world.VolumesAsync());
    }

    // The restore is the new VM's create: keyed like one, so a create that never ends shows as stuck with its command,
    // and reported through the create's route with the id the payload carries.
    [Fact]
    public async Task ARestore_SendsOneVmRestore_OfNoTenant_KeyedAsTheNewVmsCreate_WithNoVolume()
    {
        Vm source = await AVmWithAnArchiveAsync();

        Guid newId = await _world.RestoreVmAsync(source.Id, VmKey, "web-1-restored");

        Vm restored = (await _world.VmsAsync()).Single(v => v.Id == newId);
        Guid createId = restored.Operation.PendingId!.Value;
        ProviderCommand command = Assert.Single(await _world.CommandsAsync());
        Assert.Equal(ProviderCommandKinds.VmRestore, command.Kind);
        Assert.Equal($"vm-create-{newId}-{createId}", command.Key);
        Assert.Equal(source.ClusterId, command.ClusterId);
        Assert.Null(command.TenantId);
        Assert.Equal(newId, command.ResourceId);
        Assert.Equal(ProviderCommandStatus.Pending, command.Status);
        JsonElement sent = PayloadOf(command);
        Assert.Equal(newId, sent.GetProperty("vmId").GetGuid());
        Assert.Equal(createId, sent.GetProperty("requestId").GetGuid());
        Assert.Equal("web-1-restored", sent.GetProperty("name").GetString());
        Assert.Equal(source.Id, sent.GetProperty("sourceVmId").GetGuid());
        Assert.Equal(VmKey, sent.GetProperty("backupKey").GetString());
        Assert.Equal(_world.Owner.Id.ToString(), sent.GetProperty("tenantId").GetString());
        Assert.Equal(Tenant.ProjectNameFor(_world.Owner.Id), sent.GetProperty("projectName").GetString());
        Assert.Equal(source.DiskSizeGb, sent.GetProperty("diskSizeGb").GetInt32());
        Assert.Equal(JsonValueKind.Array, sent.GetProperty("volumes").ValueKind);
        Assert.Equal(0, sent.GetProperty("volumes").GetArrayLength());
    }

    [Fact]
    public async Task ARestore_IsAudited_UnderTheOwningTenant_NamingTheSourceAndTheArchive()
    {
        Vm source = await AVmWithAnArchiveAsync();

        Guid newId = await _world.RestoreVmAsync(source.Id, VmKey, "web-1-restored");

        AdminAuditLog entry = Assert.Single(await _world.AuditAsync());
        Assert.Equal("vm.restore.cross_tenant", entry.Action);
        Assert.Equal(RestoreFromBackupWorld.Admin, entry.ActorKeycloakUserId);
        Assert.Equal(("vm", newId), (entry.ResourceType, entry.ResourceId));
        Assert.Equal(_world.Owner.Id, entry.OwnerTenantId);
        Assert.NotEqual(_world.AdminsOwnTenant, entry.OwnerTenantId);
        Assert.Equal($"source={source.Id} backup={VmKey}", entry.Detail);
        string logged = Assert.Single(_world.Warnings);
        Assert.Contains("vm.restore.cross_tenant", logged);
        Assert.Contains(RestoreFromBackupWorld.Admin, logged);
        Assert.Contains(newId.ToString(), logged);
        Assert.Contains(_world.Owner.Id.ToString(), logged);
        Assert.Contains(source.Id.ToString(), logged);
        Assert.Contains(VmKey, logged);
    }

    // ---- A VM with volumes.

    private sealed record Restored(Vm Source, Volume Data, Volume Logs, string DataKey, string LogsKey, Guid NewVmId);

    // Two volumes attached, each with archives on either side of the VM's: data's closest was taken an hour after it,
    // logs' two hours before.
    private async Task<Restored> AVmWithTwoVolumesIsRestoredAsync()
    {
        Vm source = await AVmWithAnArchiveAsync();
        Volume data = await _world.AVolumeAttachedToAsync(source, "data");
        Volume logs = await _world.AVolumeAttachedToAsync(source, "logs");
        await _world.AVolumeArchiveAsync(data, TimeSpan.FromHours(-23));
        BackupRecord dataClosest = await _world.AVolumeArchiveAsync(data, TimeSpan.FromHours(1));
        BackupRecord logsClosest = await _world.AVolumeArchiveAsync(logs, TimeSpan.FromHours(-2));
        await _world.AVolumeArchiveAsync(logs, TimeSpan.FromHours(22));

        Guid newVmId = await _world.RestoreVmAsync(source.Id, VmKey, "web-1-restored");

        return new Restored(source, data, logs, dataClosest.BackupKey, logsClosest.BackupKey, newVmId);
    }

    [Fact]
    public async Task ARestore_CopiesEveryVolumeAttachedToTheVm_EachFreeAndAskedOntoTheNewVm()
    {
        Restored restored = await AVmWithTwoVolumesIsRestoredAsync();

        List<Volume> volumes = await _world.VolumesAsync();
        Assert.Equal(4, volumes.Count);
        foreach (Volume source in new[] { restored.Data, restored.Logs })
        {
            Volume copy = Assert.Single(volumes, v => v.RestoredFromBackup && v.Name == source.Name);
            Assert.NotEqual(source.Id, copy.Id);
            Assert.Equal((_world.Owner.Id, source.ClusterId, source.VolumeOfferingId, source.SizeGb),
                (copy.TenantId, copy.ClusterId, copy.VolumeOfferingId, copy.SizeGb));
            Assert.Equal(VolumeStatus.Creating, copy.Status);
            Assert.NotNull(copy.Operation.PendingId);
            Assert.Null(copy.ExternalId);
            // Asked onto the new VM; Incus has attached nothing yet.
            Assert.Equal((restored.NewVmId, null), (copy.DesiredAttachedVmId, copy.CurrentAttachedVmId));
            Assert.Equal((false, false), (copy.OccupiesQuota, copy.IsBilled));
            // The original stays attached to the original VM.
            Volume original = volumes.Single(v => v.Id == source.Id);
            Assert.Equal((restored.Source.Id, VolumeStatus.Available, false),
                (original.CurrentAttachedVmId, original.Status, original.RestoredFromBackup));
        }
    }

    // The VM's import names its volumes' copies, so each copy's restore is asked first: the lease hands out the oldest
    // row. Every copy reads the archive of its own volume taken closest to the VM's.
    [Fact]
    public async Task ARestore_AsksEveryVolumeRestoreBeforeTheVms_EachFromItsOwnClosestArchive()
    {
        Restored restored = await AVmWithTwoVolumesIsRestoredAsync();

        List<ProviderCommand> commands = await _world.CommandsAsync();
        Assert.Equal(
            [ProviderCommandKinds.VolumeRestore, ProviderCommandKinds.VolumeRestore, ProviderCommandKinds.VmRestore],
            commands.Select(c => c.Kind));
        Assert.True(commands[0].CreatedAt < commands[2].CreatedAt && commands[1].CreatedAt < commands[2].CreatedAt);
        Assert.All(commands, c => Assert.Null(c.TenantId));
        Assert.All(commands, c => Assert.Equal(restored.Source.ClusterId, c.ClusterId));

        List<Volume> copies = (await _world.VolumesAsync()).Where(v => v.RestoredFromBackup).ToList();
        foreach ((Volume source, string key) in new[] { (restored.Data, restored.DataKey), (restored.Logs, restored.LogsKey) })
        {
            Volume copy = copies.Single(v => v.Name == source.Name);
            Guid createId = copy.Operation.PendingId!.Value;
            ProviderCommand command = Assert.Single(commands, c => c.ResourceId == copy.Id);
            Assert.Equal(ProviderCommandKinds.VolumeRestore, command.Kind);
            Assert.Equal($"volume-create-{copy.Id}-{createId}", command.Key);
            JsonElement sent = PayloadOf(command);
            Assert.Equal(copy.Id, sent.GetProperty("newVolumeId").GetGuid());
            Assert.Equal(createId, sent.GetProperty("requestId").GetGuid());
            Assert.Equal(source.Name, sent.GetProperty("name").GetString());
            Assert.Equal(source.Id, sent.GetProperty("sourceVolumeId").GetGuid());
            Assert.Equal(key, sent.GetProperty("backupKey").GetString());
            Assert.Equal(_world.Owner.Id.ToString(), sent.GetProperty("tenantId").GetString());
            Assert.Equal(Tenant.ProjectNameFor(_world.Owner.Id), sent.GetProperty("projectName").GetString());
            Assert.Equal(source.SizeGb, sent.GetProperty("sizeGb").GetInt32());
        }
    }

    // The worker tells a copy whose restore ended from one still being restored by the id of the copy's create: the VM's
    // command names, for each source volume, the copy and that same id.
    [Fact]
    public async Task TheVmRestore_NamesEachCopy_WithTheRequestItsOwnCommandCarries()
    {
        Restored restored = await AVmWithTwoVolumesIsRestoredAsync();

        List<ProviderCommand> commands = await _world.CommandsAsync();
        ProviderCommand vmCommand = commands.Single(c => c.Kind == ProviderCommandKinds.VmRestore);
        Assert.Equal(restored.NewVmId, vmCommand.ResourceId);
        List<Volume> copies = (await _world.VolumesAsync()).Where(v => v.RestoredFromBackup).ToList();
        var named = PayloadOf(vmCommand).GetProperty("volumes").EnumerateArray()
            .Select(v => (Source: v.GetProperty("sourceVolumeId").GetGuid(), Copy: v.GetProperty("newVolumeId").GetGuid(),
                Request: v.GetProperty("requestId").GetGuid()))
            .ToList();
        Assert.Equal(2, named.Count);
        foreach (Volume source in new[] { restored.Data, restored.Logs })
        {
            Volume copy = copies.Single(v => v.Name == source.Name);
            var pair = Assert.Single(named, n => n.Source == source.Id);
            Assert.Equal((copy.Id, copy.Operation.PendingId!.Value), (pair.Copy, pair.Request));
            ProviderCommand volumeCommand = commands.Single(c => c.ResourceId == copy.Id);
            Assert.Equal(pair.Request, PayloadOf(volumeCommand).GetProperty("requestId").GetGuid());
        }
    }

    [Fact]
    public async Task ARestore_AuditsTheVmAndEachCopy_EachNamingItsOwnSourceAndArchive()
    {
        Restored restored = await AVmWithTwoVolumesIsRestoredAsync();

        List<AdminAuditLog> entries = await _world.AuditAsync();
        Assert.Equal(3, entries.Count);
        Assert.All(entries, e => Assert.Equal((RestoreFromBackupWorld.Admin, _world.Owner.Id), (e.ActorKeycloakUserId, e.OwnerTenantId)));
        AdminAuditLog vmEntry = Assert.Single(entries, e => e.Action == "vm.restore.cross_tenant");
        Assert.Equal(("vm", restored.NewVmId, $"source={restored.Source.Id} backup={VmKey}"),
            (vmEntry.ResourceType, vmEntry.ResourceId, vmEntry.Detail));
        List<Volume> copies = (await _world.VolumesAsync()).Where(v => v.RestoredFromBackup).ToList();
        foreach ((Volume source, string key) in new[] { (restored.Data, restored.DataKey), (restored.Logs, restored.LogsKey) })
        {
            Volume copy = copies.Single(v => v.Name == source.Name);
            AdminAuditLog entry = Assert.Single(entries, e => e.ResourceId == copy.Id);
            Assert.Equal(("volume.restore.cross_tenant", "volume", $"source={source.Id} backup={key}"),
                (entry.Action, entry.ResourceType, entry.Detail));
        }
        Assert.Equal(3, _world.Warnings.Count);
    }

    // One save: the VM, its volumes, their audit rows and their commands, or none of them. And nothing else is written:
    // no wallet, ledger or usage row, no activity under the admin's own tenant, no change to the originals.
    [Fact]
    public async Task ARestore_SavesOnce_EveryRowAuditEntryAndCommand_AndNothingElse()
    {
        await AVmWithTwoVolumesIsRestoredAsync();

        IReadOnlyList<string> written = Assert.Single(_world.Saves);
        Assert.Equal(
            [
                "Added AdminAuditLog", "Added AdminAuditLog", "Added AdminAuditLog",
                "Added ProviderCommand", "Added ProviderCommand", "Added ProviderCommand",
                "Added Vm", "Added Volume", "Added Volume",
            ],
            written);
        Assert.Empty(_world.LeftStaged);
        Assert.Equal(0, await _world.MoneyAndFeedRowsAsync());
    }

    // What goes with the VM is what Incus holds attached to it now: not a volume only asked onto it, not one attached
    // to another VM, not a free one.
    [Fact]
    public async Task OnlyTheVolumesIncusHoldsAttachedToTheVm_AreCopied()
    {
        Vm source = await AVmWithAnArchiveAsync();
        Vm other = await _world.ARunningVmAsync("web-2");
        Volume attached = await _world.AVolumeAttachedToAsync(source, "attached");
        Volume onAnotherVm = await _world.AVolumeAttachedToAsync(other, "on-another-vm");
        Volume free = await _world.AnAvailableVolumeAsync("free");
        Volume onlyAsked = RestoreFromBackupWorld.AnAvailableVolume(_world.Owner, "only-asked");
        onlyAsked.Attach(source);
        Assert.Equal((source.Id, null), (onlyAsked.DesiredAttachedVmId, onlyAsked.CurrentAttachedVmId));
        await _world.HasAsync(onlyAsked);
        // Every one has an archive: only the attachment decides.
        foreach (Volume volume in new[] { attached, onAnotherVm, free, onlyAsked })
        {
            await _world.AVolumeArchiveAsync(volume, TimeSpan.Zero);
        }

        await _world.RestoreVmAsync(source.Id, VmKey, "web-1-restored");

        Volume copy = Assert.Single(await _world.VolumesAsync(), v => v.RestoredFromBackup);
        Assert.Equal("attached", copy.Name);
        Assert.Equal(1, (await _world.CommandsAsync()).Count(c => c.Kind == ProviderCommandKinds.VolumeRestore));
    }

    // The restore asks no quota gate and its rows never count: a tenant at both caps gets it, and stays exactly at its
    // caps for its own creates.
    [Fact]
    public async Task ATenantAtItsVmAndVolumeCaps_StillGetsTheRestore_AndItsCountsDoNotMove()
    {
        Vm source = await AVmWithAnArchiveAsync();
        Volume data = await _world.AVolumeAttachedToAsync(source, "data");
        await _world.AVolumeArchiveAsync(data, TimeSpan.Zero);
        for (int i = 1; i < _world.Owner.Quota.MaxVms; i++)
        {
            await _world.ARunningVmAsync($"web-{i + 1}");
        }
        for (int i = 1; i < _world.Owner.Quota.MaxVolumes; i++)
        {
            await _world.AnAvailableVolumeAsync($"data-{i + 1}");
        }
        (int vmsBefore, int volumesBefore) = (await _world.VmsCountedTowardQuotaAsync(), await _world.VolumesCountedTowardQuotaAsync());
        Assert.Equal((_world.Owner.Quota.MaxVms, _world.Owner.Quota.MaxVolumes), (vmsBefore, volumesBefore));

        Guid newId = await _world.RestoreVmAsync(source.Id, VmKey, "web-1-restored");

        Assert.Contains(await _world.VmsAsync(), v => v.Id == newId);
        Assert.Contains(await _world.VolumesAsync(), v => v.RestoredFromBackup);
        Assert.Equal((vmsBefore, volumesBefore), (await _world.VmsCountedTowardQuotaAsync(), await _world.VolumesCountedTowardQuotaAsync()));
        Assert.Equal("VM_QUOTA_EXCEEDED",
            Assert.Throws<BusinessException>(() => TenantQuotaGate.EnsureHasRoom(_world.Owner, TenantQuotaKind.Vm, vmsBefore)).Code);
        Assert.Equal("VOLUME_QUOTA_EXCEEDED",
            Assert.Throws<BusinessException>(() => TenantQuotaGate.EnsureHasRoom(_world.Owner, TenantQuotaKind.Volume, volumesBefore)).Code);
    }

    // A name is taken only inside the tenant, and only by a VM that is not Deleted: the same rule a tenant's own create
    // follows, which is also the unique index's.
    [Fact]
    public async Task ANameADeletedVmOrAnotherTenantsVmCarries_IsFree()
    {
        Vm source = await AVmWithAnArchiveAsync();
        Vm deleted = Vm.Create(_world.Owner.Id, Guid.NewGuid(), "web-old", "m", "tpl-1", RestoreFromBackupWorld.Cluster, 20);
        deleted.MarkRunning("vm-old", "10.0.0.9");
        deleted.Delete(Guid.NewGuid());
        deleted.MarkDeleted();
        await _world.HasAsync(deleted);
        await _world.ARunningVmAsync("web-theirs", TestTenant.WithRoomToSpare());

        await _world.RestoreVmAsync(source.Id, VmKey, "web-old");
        await _world.RestoreVmAsync(source.Id, VmKey, "web-theirs");

        Assert.Equal(2, (await _world.VmsAsync()).Count(v => v.RestoredFromBackup));
    }

    // ---- Every refusal leaves nothing: no row, no audit entry, no command, nothing staged.

    private async Task<TException> RefusedAsync<TException>(Guid vmId, string backupKey, string name, string code)
        where TException : Exception
    {
        (int vmsBefore, int volumesBefore) = ((await _world.VmsAsync()).Count, (await _world.VolumesAsync()).Count);

        TException refused = await Assert.ThrowsAsync<TException>(() => _world.RestoreVmAsync(vmId, backupKey, name));

        Assert.Equal(code, RestoreFromBackupWorld.CodeOf(refused));
        Assert.Empty(_world.Saves);
        Assert.Empty(_world.LeftStaged);
        Assert.Equal((vmsBefore, volumesBefore), ((await _world.VmsAsync()).Count, (await _world.VolumesAsync()).Count));
        Assert.Empty(await _world.CommandsAsync());
        Assert.Empty(await _world.AuditAsync());
        Assert.Empty(_world.Warnings);
        return refused;
    }

    public static TheoryData<string> NamesThatBreakTheVmRule() =>
        new() { "", "a_b", "-web", new string('a', Vm.NameMaxLength + 1) };

    [Theory]
    [MemberData(nameof(NamesThatBreakTheVmRule))]
    public async Task ANameThatBreaksTheVmRule_IsRefused_BeforeAnythingIsRead(string name)
    {
        // Not even an existing VM: the name answers first.
        await RefusedAsync<BusinessException>(Guid.NewGuid(), VmKey, name, "INVALID_VM_NAME");
    }

    [Fact]
    public async Task AVmThatDoesNotExist_Is404()
    {
        await RefusedAsync<NotFoundException>(Guid.NewGuid(), VmKey, "web-1-restored", "VM_NOT_FOUND");
    }

    [Fact]
    public async Task ADeletedVm_Is404_EvenWithAnArchiveLeft()
    {
        Vm deleted = Vm.Create(_world.Owner.Id, Guid.NewGuid(), "web-1", "m", "tpl-1", RestoreFromBackupWorld.Cluster, 20);
        deleted.MarkRunning("vm-old", "10.0.0.9");
        deleted.Delete(Guid.NewGuid());
        deleted.MarkDeleted();
        await _world.HasAsync(deleted);
        await _world.AVmArchiveAsync(deleted);

        await RefusedAsync<NotFoundException>(deleted.Id, VmKey, "web-1-restored", "VM_NOT_FOUND");
    }

    [Fact]
    public async Task AKeyNoArchiveOfTheVmCarries_Is404()
    {
        Vm source = await AVmWithAnArchiveAsync();

        await RefusedAsync<NotFoundException>(source.Id, RestoreFromBackupWorld.KeyOf(_world.VmArchiveTakenAt.AddDays(-1)),
            "web-1-restored", "BACKUP_NOT_FOUND");
    }

    // The key is looked up among the VM's own archives: another VM's, or a volume's under the same id, is not one; nor is
    // one that did not complete.
    [Fact]
    public async Task AnArchiveOfAnotherResource_OrAnIncompleteOne_UnderTheSameKey_Is404()
    {
        Vm source = await _world.ARunningVmAsync();
        Vm other = await _world.ARunningVmAsync("web-2");
        await _world.AVmArchiveAsync(other);
        await _world.AnArchiveAsync(BackupRecord.VolumeKind, source.Id, _world.VmArchiveTakenAt);

        await RefusedAsync<NotFoundException>(source.Id, VmKey, "web-1-restored", "BACKUP_NOT_FOUND");

        await _world.AnArchiveAsync(BackupRecord.VmKind, source.Id, _world.VmArchiveTakenAt, BackupRecordStatus.Incomplete);
        await RefusedAsync<NotFoundException>(source.Id, VmKey, "web-1-restored", "BACKUP_NOT_FOUND");
    }

    [Fact]
    public async Task ANameAnotherVmOfTheTenantCarries_Is409_TheOriginalsOwnIncluded()
    {
        Vm source = await AVmWithAnArchiveAsync();
        await _world.ARunningVmAsync("web-2");

        await RefusedAsync<ConflictException>(source.Id, VmKey, "web-2", "VM_NAME_TAKEN");
        await RefusedAsync<ConflictException>(source.Id, VmKey, source.Name, "VM_NAME_TAKEN");
    }

    // A VM restored without one of its disks would boot into a broken mount: the restore is refused whole, and the
    // answer names the volume the admin must look at.
    [Fact]
    public async Task AnAttachedVolumeWithNoArchive_Refuses_NamingTheVolume()
    {
        Vm source = await AVmWithAnArchiveAsync();
        Volume data = await _world.AVolumeAttachedToAsync(source, "data");
        await _world.AVolumeArchiveAsync(data, TimeSpan.Zero);
        Volume logs = await _world.AVolumeAttachedToAsync(source, "logs");

        BusinessException refused = await RefusedAsync<BusinessException>(source.Id, VmKey, "web-1-restored", "BACKUP_OF_ATTACHED_VOLUME_NOT_FOUND");

        Assert.Contains("'logs'", refused.Message);
        Assert.Contains(logs.Id.ToString(), refused.Message);
    }

    // A day at most between the two archives: further apart, the disks are of two different moments of the VM.
    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public async Task AnAttachedVolumeWhoseClosestArchiveIsMoreThanADayAway_Refuses_NamingTheVolume(int sign)
    {
        Vm source = await AVmWithAnArchiveAsync();
        Volume data = await _world.AVolumeAttachedToAsync(source, "data");
        await _world.AVolumeArchiveAsync(data, (TimeSpan.FromHours(24) + TimeSpan.FromSeconds(1)) * sign);

        BusinessException refused = await RefusedAsync<BusinessException>(source.Id, VmKey, "web-1-restored", "BACKUP_OF_ATTACHED_VOLUME_NOT_FOUND");

        Assert.Contains("'data'", refused.Message);
        Assert.Contains(data.Id.ToString(), refused.Message);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public async Task AnAttachedVolumeWhoseArchiveIsExactlyADayAway_IsRestoredWithTheVm(int sign)
    {
        Vm source = await AVmWithAnArchiveAsync();
        Volume data = await _world.AVolumeAttachedToAsync(source, "data");
        await _world.AVolumeArchiveAsync(data, TimeSpan.FromHours(24) * sign);

        await _world.RestoreVmAsync(source.Id, VmKey, "web-1-restored");

        Assert.Single(await _world.VolumesAsync(), v => v.RestoredFromBackup);
    }

    // Only a VM the Backups page lists is restored: one that still has an instance. Its archive alone is not enough.
    [Fact]
    public async Task AVmWhoseInstanceIsGone_IsRefused_EvenWithItsArchive()
    {
        Vm gone = Vm.Create(_world.Owner.Id, Guid.NewGuid(), "web-1", "m", "tpl-1", RestoreFromBackupWorld.Cluster, 20);
        gone.MarkRunning("vm-gone", "10.0.0.9");
        gone.MarkNotFound();
        Assert.False(gone.HasAnInstance);
        await _world.HasAsync(gone);
        await _world.AVmArchiveAsync(gone);

        await RefusedAsync<BusinessException>(gone.Id, VmKey, "web-1-restored", "BACKUP_SOURCE_INVALID_STATE");
    }

    // The last check is the rows' own: a copy its volume refuses takes the VM and every other copy with it, so no row
    // is added before all of them are built. The volume here is one Incus was seen holding, attached to the VM, before
    // any report brought its provider id: there is nothing a backup of it could be restored beside.
    [Fact]
    public async Task AnAttachedVolumeThatRefusesItsCopy_RefusesTheWholeRestore_TheVmAndTheOtherCopiesIncluded()
    {
        Vm source = await AVmWithAnArchiveAsync();
        Volume data = await _world.AVolumeAttachedToAsync(source, "data");
        await _world.AVolumeArchiveAsync(data, TimeSpan.Zero);
        Volume unnamed = Volume.Create(_world.Owner.Id, "logs", RestoreFromBackupWorld.Cluster, "std", 50);
        unnamed.MarkObserved(source.Id, attachedElsewhere: false);
        Assert.Equal((source.Id, true, null), (unnamed.CurrentAttachedVmId, unnamed.IsOnIncus, unnamed.ExternalId));
        // Made after "data", so its copy is the last one built: the VM's and data's are there to be left behind.
        await _world.HasAsync(unnamed);
        await _world.AVolumeArchiveAsync(unnamed, TimeSpan.Zero);

        await RefusedAsync<BusinessException>(source.Id, VmKey, "web-1-restored", "BACKUP_SOURCE_INVALID_STATE");
    }

    // ---- The order of the answers, which is the order of the checks.

    [Fact]
    public async Task AMissingArchive_AnswersBeforeATakenName()
    {
        Vm source = await _world.ARunningVmAsync();

        await RefusedAsync<NotFoundException>(source.Id, VmKey, source.Name, "BACKUP_NOT_FOUND");
    }

    [Fact]
    public async Task ATakenName_AnswersBeforeAVolumeWithNoArchive()
    {
        Vm source = await AVmWithAnArchiveAsync();
        await _world.AVolumeAttachedToAsync(source, "data");

        await RefusedAsync<ConflictException>(source.Id, VmKey, source.Name, "VM_NAME_TAKEN");
    }
}
