using System.Text.Json;
using Titan.Application.Services.Platform.Tenant;
using Titan.Domain.Aggregates.AdminAuditLog;
using Titan.Domain.Aggregates.BackupRecord;
using Titan.Domain.Aggregates.ProviderCommand;
using Titan.Domain.Aggregates.Tenant;
using Titan.Domain.Aggregates.Volume;
using Titan.Domain.Exceptions;

namespace Titan.Tests.Services;

// A platform admin restores one backup archive of a volume as a new volume of the same tenant, next to the original
// (titan-ops features/backup/design.md#restore-as-new-resource): a row the tenant sees and may delete, that never counts
// toward its quota and is never billed, restored by one volume.restore command of no tenant.
public class RestoreVolumeFromBackupTests
{
    private readonly RestoreFromBackupWorld _world = new();
    private static readonly DateTimeOffset TakenAt = new(2026, 11, 20, 1, 30, 0, TimeSpan.Zero);
    private static readonly string Key = RestoreFromBackupWorld.KeyOf(TakenAt);

    private async Task<Volume> AVolumeWithAnArchiveAsync()
    {
        Volume source = await _world.AnAvailableVolumeAsync("data-1");
        await _world.AnArchiveAsync(BackupRecord.VolumeKind, source.Id, TakenAt);
        return source;
    }

    // ---- The restore.

    [Fact]
    public async Task ARestore_MakesANewFreeVolumeOfTheSameTenant_WaitingOnItsCreate()
    {
        Volume source = await AVolumeWithAnArchiveAsync();

        Guid newId = await _world.RestoreVolumeAsync(source.Id, Key, "data-1-restored");

        List<Volume> volumes = await _world.VolumesAsync();
        Assert.Equal(2, volumes.Count);
        Volume restored = volumes.Single(v => v.Id == newId);
        Assert.NotEqual(source.Id, restored.Id);
        Assert.True(restored.RestoredFromBackup);
        Assert.Equal("data-1-restored", restored.Name);
        Assert.Equal((_world.Owner.Id, source.ClusterId, source.VolumeOfferingId, source.SizeGb),
            (restored.TenantId, restored.ClusterId, restored.VolumeOfferingId, restored.SizeGb));
        Assert.Equal(VolumeStatus.Creating, restored.Status);
        Assert.NotNull(restored.Operation.PendingId);
        Assert.Null(restored.ExternalId);
        Assert.Equal((null, null), (restored.DesiredAttachedVmId, restored.CurrentAttachedVmId));
        Assert.Equal((false, false), (restored.OccupiesQuota, restored.IsBilled));
        // The original is as it was.
        Volume original = volumes.Single(v => v.Id == source.Id);
        Assert.Equal((VolumeStatus.Available, "data-1", false), (original.Status, original.Name, original.RestoredFromBackup));
        Assert.Null(original.Operation.PendingId);
    }

    // The restore is the new volume's create: keyed like one, so a create that never ends shows as stuck with its command,
    // and reported through the create's route with the id the payload carries.
    [Fact]
    public async Task ARestore_SendsOneVolumeRestore_OfNoTenant_KeyedAsTheNewVolumesCreate()
    {
        Volume source = await AVolumeWithAnArchiveAsync();

        Guid newId = await _world.RestoreVolumeAsync(source.Id, Key, "data-1-restored");

        Volume restored = (await _world.VolumesAsync()).Single(v => v.Id == newId);
        Guid createId = restored.Operation.PendingId!.Value;
        ProviderCommand command = Assert.Single(await _world.CommandsAsync());
        Assert.Equal(ProviderCommandKinds.VolumeRestore, command.Kind);
        Assert.Equal($"volume-create-{newId}-{createId}", command.Key);
        Assert.Equal(source.ClusterId, command.ClusterId);
        Assert.Null(command.TenantId);
        Assert.Equal(newId, command.ResourceId);
        Assert.Equal(ProviderCommandStatus.Pending, command.Status);
        using JsonDocument payload = JsonDocument.Parse(command.Payload);
        JsonElement sent = payload.RootElement;
        Assert.Equal(newId, sent.GetProperty("newVolumeId").GetGuid());
        Assert.Equal(createId, sent.GetProperty("requestId").GetGuid());
        Assert.Equal("data-1-restored", sent.GetProperty("name").GetString());
        Assert.Equal(source.Id, sent.GetProperty("sourceVolumeId").GetGuid());
        Assert.Equal(Key, sent.GetProperty("backupKey").GetString());
        Assert.Equal(_world.Owner.Id.ToString(), sent.GetProperty("tenantId").GetString());
        Assert.Equal(Tenant.ProjectNameFor(_world.Owner.Id), sent.GetProperty("projectName").GetString());
        Assert.Equal(source.SizeGb, sent.GetProperty("sizeGb").GetInt32());
    }

    // The trace names who restored what for whom, under the tenant that owns the volume, not the admin's own
    // (titan-ops architecture/tenancy-and-admin.md#admin-audit).
    [Fact]
    public async Task ARestore_IsAudited_UnderTheOwningTenant_NamingTheSourceAndTheArchive()
    {
        Volume source = await AVolumeWithAnArchiveAsync();

        Guid newId = await _world.RestoreVolumeAsync(source.Id, Key, "data-1-restored");

        AdminAuditLog entry = Assert.Single(await _world.AuditAsync());
        Assert.Equal("volume.restore.cross_tenant", entry.Action);
        Assert.Equal(RestoreFromBackupWorld.Admin, entry.ActorKeycloakUserId);
        Assert.Equal(("volume", newId), (entry.ResourceType, entry.ResourceId));
        Assert.Equal(_world.Owner.Id, entry.OwnerTenantId);
        Assert.NotEqual(_world.AdminsOwnTenant, entry.OwnerTenantId);
        Assert.Equal($"source={source.Id} backup={Key}", entry.Detail);
        string logged = Assert.Single(_world.Warnings);
        Assert.Contains("volume.restore.cross_tenant", logged);
        Assert.Contains(RestoreFromBackupWorld.Admin, logged);
        Assert.Contains(newId.ToString(), logged);
        Assert.Contains(_world.Owner.Id.ToString(), logged);
        Assert.Contains(source.Id.ToString(), logged);
        Assert.Contains(Key, logged);
    }

    // One save: a volume without its command would wait for ever, a command without its audit row would restore a
    // tenant's data off the record. And nothing else is written: no wallet, ledger or usage row, no activity under the
    // admin's own tenant, no change to the original.
    [Fact]
    public async Task ARestore_SavesOnce_TheVolumeItsAuditRowAndItsCommand_AndNothingElse()
    {
        Volume source = await AVolumeWithAnArchiveAsync();

        await _world.RestoreVolumeAsync(source.Id, Key, "data-1-restored");

        IReadOnlyList<string> written = Assert.Single(_world.Saves);
        Assert.Equal(["Added AdminAuditLog", "Added ProviderCommand", "Added Volume"], written);
        Assert.Empty(_world.LeftStaged);
        Assert.Equal(0, await _world.MoneyAndFeedRowsAsync());
    }

    // The restore asks no quota gate and its volume never counts: a tenant at its cap gets it, and stays exactly at its
    // cap for its own creates.
    [Fact]
    public async Task ATenantAtItsVolumeCap_StillGetsTheRestore_AndItsCountDoesNotMove()
    {
        Volume source = await AVolumeWithAnArchiveAsync();
        for (int i = 1; i < _world.Owner.Quota.MaxVolumes; i++)
        {
            await _world.AnAvailableVolumeAsync($"data-{i + 1}");
        }
        int before = await _world.VolumesCountedTowardQuotaAsync();
        Assert.Equal(_world.Owner.Quota.MaxVolumes, before);

        Guid newId = await _world.RestoreVolumeAsync(source.Id, Key, "data-1-restored");

        Assert.Contains(await _world.VolumesAsync(), v => v.Id == newId);
        int after = await _world.VolumesCountedTowardQuotaAsync();
        Assert.Equal(before, after);
        var refused = Assert.Throws<BusinessException>(
            () => TenantQuotaGate.EnsureHasRoom(_world.Owner, TenantQuotaKind.Volume, after));
        Assert.Equal("VOLUME_QUOTA_EXCEEDED", refused.Code);
    }

    // Volume names are not unique: a copy may carry its original's name.
    [Fact]
    public async Task ARestore_UnderTheOriginalsName_IsAccepted()
    {
        Volume source = await AVolumeWithAnArchiveAsync();

        Guid newId = await _world.RestoreVolumeAsync(source.Id, Key, source.Name);

        Assert.Equal(2, (await _world.VolumesAsync()).Count(v => v.Name == source.Name));
        Assert.NotEqual(source.Id, newId);
    }

    // An older archive than the newest is restored as readily: the admin names the one.
    [Fact]
    public async Task ARestore_ReadsTheArchiveTheAdminNamed_NotTheNewest()
    {
        Volume source = await AVolumeWithAnArchiveAsync();
        await _world.AnArchiveAsync(BackupRecord.VolumeKind, source.Id, TakenAt.AddDays(1));

        await _world.RestoreVolumeAsync(source.Id, Key, "data-1-restored");

        ProviderCommand command = Assert.Single(await _world.CommandsAsync());
        using JsonDocument payload = JsonDocument.Parse(command.Payload);
        Assert.Equal(Key, payload.RootElement.GetProperty("backupKey").GetString());
    }

    // ---- Every refusal leaves nothing: no row, no audit entry, no command, nothing staged.

    private async Task<TException> RefusedAsync<TException>(Guid volumeId, string backupKey, string name, string code)
        where TException : Exception
    {
        int volumesBefore = (await _world.VolumesAsync()).Count;

        TException refused = await Assert.ThrowsAsync<TException>(() => _world.RestoreVolumeAsync(volumeId, backupKey, name));

        Assert.Equal(code, RestoreFromBackupWorld.CodeOf(refused));
        Assert.Empty(_world.Saves);
        Assert.Empty(_world.LeftStaged);
        Assert.Equal(volumesBefore, (await _world.VolumesAsync()).Count);
        Assert.Empty(await _world.CommandsAsync());
        Assert.Empty(await _world.AuditAsync());
        Assert.Empty(_world.Warnings);
        return refused;
    }

    public static TheoryData<string> NamesThatBreakTheVolumeRule() =>
        new() { "", "   ", new string('a', Volume.NameMaxLength + 1) };

    [Theory]
    [MemberData(nameof(NamesThatBreakTheVolumeRule))]
    public async Task ANameThatBreaksTheVolumeRule_IsRefused_BeforeAnythingIsRead(string name)
    {
        // Not even an existing volume: the name answers first.
        await RefusedAsync<BusinessException>(Guid.NewGuid(), Key, name, "INVALID_VOLUME_NAME");
    }

    [Fact]
    public async Task AVolumeThatDoesNotExist_Is404()
    {
        await RefusedAsync<NotFoundException>(Guid.NewGuid(), Key, "data-1-restored", "VOLUME_NOT_FOUND");
    }

    [Fact]
    public async Task ADeletedVolume_Is404_EvenWithAnArchiveLeft()
    {
        Volume deleted = RestoreFromBackupWorld.AnAvailableVolume(_world.Owner);
        deleted.Delete();
        deleted.MarkDeleted();
        Assert.True(deleted.IsDeleted);
        await _world.HasAsync(deleted);
        await _world.AnArchiveAsync(BackupRecord.VolumeKind, deleted.Id, TakenAt);

        await RefusedAsync<NotFoundException>(deleted.Id, Key, "data-1-restored", "VOLUME_NOT_FOUND");
    }

    [Fact]
    public async Task AKeyNoArchiveOfTheVolumeCarries_Is404()
    {
        Volume source = await AVolumeWithAnArchiveAsync();

        await RefusedAsync<NotFoundException>(source.Id, RestoreFromBackupWorld.KeyOf(TakenAt.AddDays(-1)), "data-1-restored", "BACKUP_NOT_FOUND");
    }

    // The key is looked up among the volume's own archives: another volume's, or a VM's under the same id, is not one.
    [Fact]
    public async Task AnArchiveOfAnotherResource_UnderTheSameKey_Is404()
    {
        Volume source = await _world.AnAvailableVolumeAsync("data-1");
        Volume other = await _world.AnAvailableVolumeAsync("data-2");
        await _world.AnArchiveAsync(BackupRecord.VolumeKind, other.Id, TakenAt);
        await _world.AnArchiveAsync(BackupRecord.VmKind, source.Id, TakenAt);

        await RefusedAsync<NotFoundException>(source.Id, Key, "data-1-restored", "BACKUP_NOT_FOUND");
    }

    [Fact]
    public async Task AnArchiveThatIsNotComplete_Is404()
    {
        Volume source = await _world.AnAvailableVolumeAsync("data-1");
        await _world.AnArchiveAsync(BackupRecord.VolumeKind, source.Id, TakenAt, BackupRecordStatus.Incomplete);

        await RefusedAsync<NotFoundException>(source.Id, Key, "data-1-restored", "BACKUP_NOT_FOUND");
    }

    // Only a volume the Backups page lists is restored: one Incus still holds. Its archive alone is not enough.
    [Fact]
    public async Task AVolumeIncusNoLongerHolds_IsRefused_EvenWithItsArchive()
    {
        Volume gone = RestoreFromBackupWorld.AnAvailableVolume(_world.Owner);
        gone.MarkNotFound();
        Assert.False(gone.IsOnIncus);
        await _world.HasAsync(gone);
        await _world.AnArchiveAsync(BackupRecord.VolumeKind, gone.Id, TakenAt);

        await RefusedAsync<BusinessException>(gone.Id, Key, "data-1-restored", "BACKUP_SOURCE_INVALID_STATE");
    }

    // The order of the answers: the volume before its archive, the archive before the volume's state.
    [Fact]
    public async Task AVolumeIncusNoLongerHolds_WithoutTheArchive_AnswersTheArchive()
    {
        Volume gone = RestoreFromBackupWorld.AnAvailableVolume(_world.Owner);
        gone.MarkNotFound();
        await _world.HasAsync(gone);

        await RefusedAsync<NotFoundException>(gone.Id, Key, "data-1-restored", "BACKUP_NOT_FOUND");
    }
}
