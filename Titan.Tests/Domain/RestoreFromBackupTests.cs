using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Aggregates.Volume;
using Titan.Domain.Exceptions;

namespace Titan.Tests.Domain;

// A VM or volume an admin restores from a backup archive is a new row of the same tenant, built from the row the archive
// was taken of: it waits on its own create like any other, and it never counts toward the tenant's quota nor is billed
// (titan-ops features/backup/design.md#restore-as-new-resource).
public class RestoreFromBackupTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Network = Guid.NewGuid();

    private static Vm NewVm(string name = "web-1") =>
        Vm.Create(Tenant, Network, name, "m", "tpl-1", "cluster-a", 40, userData: "#cloud-config", stopTimeoutSeconds: 600);

    // A VM in each situation the tests name, reached by the reports and observations that lead there.
    private static Vm Brought(Vm vm, string situation)
    {
        switch (situation)
        {
            case "creating":
                break;
            case "create-failed":
                vm.MarkRequestFailed("refused", null);
                break;
            case "create-failed-with-instance":
                vm.MarkCreated("vm-" + vm.Id.ToString("N")[..8]);
                vm.MarkRequestFailed("refused", null);
                break;
            case "running":
                vm.MarkRunning("vm-" + vm.Id.ToString("N")[..8], "10.0.0.5");
                break;
            case "ready":
            case "stopped":
            case "frozen":
            case "error":
                vm.MarkRunning("vm-" + vm.Id.ToString("N")[..8], "10.0.0.5");
                vm.MarkObserved(Enum.Parse<VmStatus>(situation, ignoreCase: true), null);
                break;
            case "not-found":
                vm.MarkRunning("vm-" + vm.Id.ToString("N")[..8], "10.0.0.5");
                vm.MarkNotFound();
                break;
            case "deleting":
                vm.MarkRunning("vm-" + vm.Id.ToString("N")[..8], "10.0.0.5");
                vm.Delete(Guid.NewGuid());
                break;
            case "deleted":
                vm.MarkRunning("vm-" + vm.Id.ToString("N")[..8], "10.0.0.5");
                vm.Delete(Guid.NewGuid());
                vm.MarkDeleted();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(situation), situation, "Not a situation this test knows.");
        }
        return vm;
    }

    private static Vm RestoredVm(string name = "web-1-restored") => Vm.RestoreFromBackup(Brought(NewVm(), "running"), name);

    // ---- VM

    [Fact]
    public void ARestoredVm_IsANewVmOfTheSameTenant_BuiltLikeItsSource_WaitingOnItsOwnCreate()
    {
        Vm source = Brought(NewVm(), "running");

        Vm restored = Vm.RestoreFromBackup(source, "web-1-restored");

        Assert.True(restored.RestoredFromBackup);
        Assert.NotEqual(Guid.Empty, restored.Id);
        Assert.NotEqual(source.Id, restored.Id);
        Assert.Equal("web-1-restored", restored.Name);
        Assert.Equal(
            (source.TenantId, source.NetworkId, source.VmOfferingId, source.TemplateId, source.ClusterId, source.DiskSizeGb),
            (restored.TenantId, restored.NetworkId, restored.VmOfferingId, restored.TemplateId, restored.ClusterId, restored.DiskSizeGb));
        Assert.Equal((600, "#cloud-config"), (restored.StopTimeoutSeconds, restored.UserData));
        Assert.Equal(VmStatus.Creating, restored.Status);
        Assert.NotNull(restored.Operation.PendingId);
        Assert.NotEqual(Guid.Empty, restored.Operation.PendingId);
        Assert.NotNull(restored.Operation.AskedAt);
        // Nothing of the source's instance comes along: the worker's report names the new one.
        Assert.Null(restored.ExternalId);
        Assert.Null(restored.IpAddress);
        Assert.Null(restored.PublicIpv6Address);
        Assert.Null(restored.InstanceStatus);
        Assert.Empty(restored.Snapshots);
    }

    [Fact]
    public void RestoringAVm_ChangesNothingOnItsSource()
    {
        Vm source = Brought(NewVm(), "running");
        VmOperation operation = source.Operation;

        Vm.RestoreFromBackup(source, "web-1-restored");

        Assert.False(source.RestoredFromBackup);
        Assert.Equal((VmStatus.Running, "web-1", operation), (source.Status, source.Name, source.Operation));
    }

    [Fact]
    public void TwoRestoresOfOneVm_AreTwoVms_EachWithItsOwnCreate()
    {
        Vm source = Brought(NewVm(), "stopped");

        Vm first = Vm.RestoreFromBackup(source, "copy-1");
        Vm second = Vm.RestoreFromBackup(source, "copy-2");

        Assert.NotEqual(first.Id, second.Id);
        Assert.NotEqual(first.Operation.PendingId, second.Operation.PendingId);
    }

    [Fact]
    public void ACreatedVm_IsNotRestored()
    {
        Assert.False(NewVm().RestoredFromBackup);
    }

    // The Backups page lists a VM while it has an instance (Vm.HasAnInstance): only those are restored.
    [Theory]
    [InlineData("running")]
    [InlineData("stopped")]
    [InlineData("error")]
    [InlineData("create-failed-with-instance")]
    [InlineData("deleting")]
    public void AVmWithAnInstance_CanBeRestored(string situation)
    {
        Vm source = Brought(NewVm(), situation);
        Assert.True(source.HasAnInstance);

        Assert.True(Vm.RestoreFromBackup(source, "copy").RestoredFromBackup);
    }

    [Theory]
    [InlineData("creating")]
    [InlineData("create-failed")]
    [InlineData("not-found")]
    [InlineData("deleted")]
    public void AVmWithoutAnInstance_IsRefused_BackupSourceInvalidState(string situation)
    {
        Vm source = Brought(NewVm(), situation);
        Assert.False(source.HasAnInstance);

        var ex = Assert.Throws<BusinessException>(() => Vm.RestoreFromBackup(source, "copy"));

        Assert.Equal("BACKUP_SOURCE_INVALID_STATE", ex.Code);
    }

    // Each situation beside an ordinary VM in the same one: the flag alone makes the difference.
    [Theory]
    [InlineData("creating", true, false)]
    [InlineData("stopped", true, false)]
    [InlineData("running", true, true)]
    [InlineData("ready", true, true)]
    [InlineData("frozen", true, true)]
    [InlineData("error", true, true)]
    [InlineData("create-failed-with-instance", true, false)]
    [InlineData("deleting", false, true)]
    public void ARestoredVm_NeverOccupiesQuota_NorIsBilled_WhereAnOrdinaryOneDoes(
        string situation, bool ordinaryOccupies, bool ordinaryIsBilled)
    {
        Vm ordinary = Brought(NewVm(), situation);
        Vm restored = Brought(RestoredVm(), situation);

        Assert.Equal(ordinary.Status, restored.Status);
        Assert.Equal((ordinaryOccupies, ordinaryIsBilled), (ordinary.OccupiesQuota, ordinary.IsBilled));
        Assert.Equal((false, false), (restored.OccupiesQuota, restored.IsBilled));
    }

    // Free is all the flag means: the instance is on Incus like any other, so its network is held and its disk is backed up.
    [Fact]
    public void ARestoredVm_HoldsItsNetwork_AndHasAnInstance_LikeAnyOther()
    {
        Vm waiting = RestoredVm();
        Vm stopped = Brought(RestoredVm(), "stopped");

        Assert.Equal((true, false), (waiting.HasNetwork, waiting.HasAnInstance));
        Assert.Equal((true, true), (stopped.HasNetwork, stopped.HasAnInstance));
    }

    // The worker reports a restore through the create route: the import leaves the instance stopped.
    [Fact]
    public void ARestoredVm_EndsItsCreateLikeAnyOther_AndStaysRestored()
    {
        Vm restored = RestoredVm();

        bool ended = restored.CreateSucceeded(restored.Operation.PendingId!.Value, "vm-new", VmStatus.Stopped, null, null);

        Assert.True(ended);
        Assert.Equal((VmStatus.Stopped, "vm-new", true), (restored.Status, restored.ExternalId, restored.RestoredFromBackup));
        Assert.Null(restored.Operation.PendingId);
    }

    [Fact]
    public void ARestoredVm_WhoseImportGaveUpBeforeIncusHeldIt_IsFailed()
    {
        Vm restored = RestoredVm();

        bool ended = restored.CreateFailed(restored.Operation.PendingId!.Value, null, "restore failed", "raw");

        Assert.True(ended);
        Assert.Equal((VmStatus.Failed, true), (restored.Status, restored.RestoredFromBackup));
    }

    // ---- Volume

    private static Volume NewVolume(string name = "data-1") => Volume.Create(Tenant, name, "cluster-a", "standard", 50);

    private static Vm RunningVm() => Brought(Vm.Create(Tenant, Network, "host", "m", "tpl-1", "cluster-a", 20), "running");

    private static Volume Brought(Volume volume, string situation)
    {
        string externalId = "vol-" + volume.Id.ToString("N")[..8];
        switch (situation)
        {
            case "creating":
                break;
            // Seen on Incus while its create still waits: Incus holds it, and no report has named it yet.
            case "creating-seen":
                volume.MarkObserved(null, false);
                break;
            case "create-failed":
                volume.MarkRequestFailed("refused", null);
                break;
            case "available":
                volume.MarkCreated(externalId);
                break;
            case "attaching":
                volume.MarkCreated(externalId);
                volume.Attach(RunningVm());
                break;
            case "attaching-lost":
                volume.MarkCreated(externalId);
                volume.Attach(RunningVm());
                volume.MarkNotFound();
                break;
            case "attached":
            case "detaching":
                volume.MarkCreated(externalId);
                Vm vm = RunningVm();
                volume.Attach(vm);
                volume.MarkAttached(vm.Id);
                if (situation == "detaching")
                {
                    volume.Detach();
                }
                break;
            case "deleting":
                volume.MarkCreated(externalId);
                volume.Delete();
                break;
            case "deleting-lost":
                volume.MarkCreated(externalId);
                volume.Delete();
                volume.MarkNotFound();
                break;
            case "not-found":
                volume.MarkCreated(externalId);
                volume.MarkNotFound();
                break;
            case "deleted":
                volume.MarkCreated(externalId);
                volume.Delete();
                volume.MarkDeleted();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(situation), situation, "Not a situation this test knows.");
        }
        return volume;
    }

    private static Volume RestoredVolume(Guid? attachedToVmId = null) =>
        Volume.RestoreFromBackup(Brought(NewVolume(), "available"), "data-1", attachedToVmId);

    [Fact]
    public void ARestoredVolume_IsANewVolumeOfTheSameTenant_BuiltLikeItsSource_WaitingOnItsOwnCreate()
    {
        Volume source = Brought(NewVolume(), "available");

        Volume restored = Volume.RestoreFromBackup(source, "data-1-restored");

        Assert.True(restored.RestoredFromBackup);
        Assert.NotEqual(Guid.Empty, restored.Id);
        Assert.NotEqual(source.Id, restored.Id);
        Assert.Equal("data-1-restored", restored.Name);
        Assert.Equal(
            (source.TenantId, source.ClusterId, source.VolumeOfferingId, source.SizeGb),
            (restored.TenantId, restored.ClusterId, restored.VolumeOfferingId, restored.SizeGb));
        Assert.Equal(VolumeStatus.Creating, restored.Status);
        Assert.NotNull(restored.Operation.PendingId);
        Assert.NotEqual(Guid.Empty, restored.Operation.PendingId);
        Assert.NotNull(restored.Operation.AskedAt);
        Assert.Null(restored.ExternalId);
        Assert.Null(restored.InstanceStatus);
        Assert.Null(restored.CurrentAttachedVmId);
        Assert.False(restored.CurrentAttachedElsewhere);
        Assert.Null(restored.DesiredAttachedVmId);
        Assert.Empty(restored.Snapshots);
    }

    // A volume restored with its VM is asked to be attached to the new VM, not to the one its source is on.
    [Fact]
    public void ARestoredVolume_ThatGoesWithAVm_IsAskedToBeAttachedToThatVm()
    {
        Volume source = Brought(NewVolume(), "attached");
        Guid newVmId = Guid.NewGuid();

        Volume restored = Volume.RestoreFromBackup(source, source.Name, newVmId);

        Assert.Equal(newVmId, restored.DesiredAttachedVmId);
        Assert.Null(restored.CurrentAttachedVmId);
        Assert.NotEqual(newVmId, source.DesiredAttachedVmId);
    }

    [Fact]
    public void RestoringAVolume_ChangesNothingOnItsSource()
    {
        Volume source = Brought(NewVolume(), "attached");
        (VolumeStatus, Guid?, Guid?, VolumeOperation) before =
            (source.Status, source.CurrentAttachedVmId, source.DesiredAttachedVmId, source.Operation);

        Volume.RestoreFromBackup(source, "copy", Guid.NewGuid());

        Assert.False(source.RestoredFromBackup);
        Assert.Equal(before, (source.Status, source.CurrentAttachedVmId, source.DesiredAttachedVmId, source.Operation));
    }

    [Fact]
    public void ACreatedVolume_IsNotRestored()
    {
        Assert.False(NewVolume().RestoredFromBackup);
    }

    // The Backups page lists a volume Incus holds as Available under a provider id: only those are restored.
    [Theory]
    [InlineData("available")]
    [InlineData("attaching")]
    [InlineData("attached")]
    [InlineData("detaching")]
    [InlineData("deleting")]
    public void AVolumeIncusHolds_CanBeRestored(string situation)
    {
        Volume source = Brought(NewVolume(), situation);
        Assert.True(source.IsOnIncus);

        Assert.True(Volume.RestoreFromBackup(source, "copy").RestoredFromBackup);
    }

    [Theory]
    [InlineData("creating")]
    [InlineData("creating-seen")]
    [InlineData("create-failed")]
    [InlineData("attaching-lost")]
    [InlineData("deleting-lost")]
    [InlineData("not-found")]
    [InlineData("deleted")]
    public void AVolumeIncusDoesNotHoldUnderAProviderId_IsRefused_BackupSourceInvalidState(string situation)
    {
        Volume source = Brought(NewVolume(), situation);

        var ex = Assert.Throws<BusinessException>(() => Volume.RestoreFromBackup(source, "copy"));

        Assert.Equal("BACKUP_SOURCE_INVALID_STATE", ex.Code);
    }

    // Each situation beside an ordinary volume in the same one: quota and billing follow the flag, "Incus holds it"
    // does not, so the backup of a restored volume goes on.
    [Theory]
    [InlineData("creating", true, false, false)]
    [InlineData("creating-seen", true, true, true)]
    [InlineData("available", true, true, true)]
    [InlineData("attaching", true, true, true)]
    [InlineData("attaching-lost", false, false, false)]
    [InlineData("attached", true, true, true)]
    [InlineData("detaching", true, true, true)]
    [InlineData("deleting", false, true, true)]
    [InlineData("deleting-lost", false, false, false)]
    [InlineData("not-found", false, false, false)]
    [InlineData("deleted", false, false, false)]
    public void ARestoredVolume_NeverOccupiesQuota_NorIsBilled_WhereAnOrdinaryOneDoes_AndIsOnIncusAlike(
        string situation, bool ordinaryOccupies, bool ordinaryIsBilled, bool onIncus)
    {
        Volume ordinary = Brought(NewVolume(), situation);
        Volume restored = Brought(RestoredVolume(), situation);

        Assert.Equal(ordinary.Status, restored.Status);
        Assert.Equal((ordinaryOccupies, ordinaryIsBilled, onIncus), (ordinary.OccupiesQuota, ordinary.IsBilled, ordinary.IsOnIncus));
        Assert.Equal((false, false, onIncus), (restored.OccupiesQuota, restored.IsBilled, restored.IsOnIncus));
    }

    // A volume's Done carries no reading of the volume: Available, and the attachment it was asked for is left as asked.
    [Fact]
    public void ARestoredVolume_EndsItsCreateLikeAnyOther_AndKeepsTheAttachmentItWasAskedFor()
    {
        Guid newVmId = Guid.NewGuid();
        Volume restored = RestoredVolume(newVmId);

        bool ended = restored.CreateSucceeded(restored.Operation.PendingId!.Value, "vol-new", false, null, false);

        Assert.True(ended);
        Assert.Equal((VolumeStatus.Available, "vol-new", true), (restored.Status, restored.ExternalId, restored.RestoredFromBackup));
        Assert.Equal(newVmId, restored.DesiredAttachedVmId);
        Assert.Null(restored.CurrentAttachedVmId);
    }

    // Only the restored volume itself is free: a snapshot the tenant takes of it is counted and billed as any other.
    [Fact]
    public void ASnapshotOfARestoredVolume_IsCountedAndBilled_LikeAnyOther()
    {
        Volume restored = Brought(RestoredVolume(), "available");

        VolumeSnapshot snapshot = restored.WithSnapshot("before-change");
        snapshot.MarkCreated("vol-new/before-change");

        Assert.True(snapshot.IsBilled);
        Assert.Equal(1, restored.SnapshotQuotaUsage);
        Assert.False(restored.IsBilled);
    }

    [Fact]
    public void ASnapshotOfARestoredVm_IsCountedAndBilled_LikeAnyOther()
    {
        Vm restored = Brought(RestoredVm(), "running");

        VmSnapshot snapshot = restored.WithSnapshot("before-change");
        restored.MarkSnapshotCreated(snapshot.Id, "vm-new/before-change");

        Assert.True(snapshot.IsBilled);
        Assert.Equal(1, restored.SnapshotQuotaUsage);
        Assert.Equal((VmStatus.Running, false), (restored.Status, restored.IsBilled));
    }
}
