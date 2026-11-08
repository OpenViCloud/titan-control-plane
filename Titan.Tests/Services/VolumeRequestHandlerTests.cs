using Moq;
using Titan.Application.Commands.Compute.Volume;
using Titan.Application.Commands.Compute.Volume.Contracts;
using Titan.Application.Commands.Compute.VolumeSnapshot;
using Titan.Application.Commands.Compute.VolumeSnapshot.Contracts;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Aggregates.Volume;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Domain.Values;
using Titan.Tests.Fakes;
using Titan.Application.Exceptions;

namespace Titan.Tests.Services;

// The request routes of titan-ops architecture/resource-lifecycle.md#callback-routes for a volume and its snapshots, at the
// handler: a report ends the request it names and records its outcome once; an observation writes what Incus holds and
// ends nothing.
public class VolumeRequestHandlerTests
{
    private static readonly Titan.Domain.Aggregates.Tenant.Tenant Owner = TestTenant.WithRoomToSpare();

    private readonly Mock<IVolumeRepository> _repo = new();
    private readonly Mock<IVmRepository> _vmRepo = new();
    private readonly FakeActivityRecorder _activity = new();
    private readonly Vm _vm = RunningVm();

    public VolumeRequestHandlerTests()
    {
        _vmRepo.Setup(r => r.ListByExternalIdsAsync(Owner.Id, It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid _, IReadOnlyCollection<string> names, CancellationToken _) =>
                names.Contains(_vm.ExternalId!) ? [_vm] : []);
    }

    private EndVolumeRequestCommandHandler EndHandler() => new(_repo.Object, _vmRepo.Object, _activity);
    private ObserveVolumeCommandHandler ObserveHandler() => new(_repo.Object, _vmRepo.Object);
    private EndVolumeSnapshotRequestCommandHandler EndSnapshotHandler() => new(_repo.Object, _activity);
    private ObserveVolumeSnapshotCommandHandler ObserveSnapshotHandler() => new(_repo.Object);

    private static Vm RunningVm()
    {
        Vm vm = Vm.Create(Owner.Id, Guid.NewGuid(), "vm-1", "m", "tpl-1", "cluster-a", 20);
        vm.MarkRunning("vm-ext", "10.0.0.5");
        return vm;
    }

    private Volume Given(Volume volume)
    {
        _repo.Setup(r => r.FindByIdForUpdateAsync(volume.Id, It.IsAny<CancellationToken>())).ReturnsAsync(volume);
        return volume;
    }

    private static Volume NewVolume() => Volume.Create(Owner.Id, "data-1", "cluster-a", "std", 20);

    private static Volume RestingVolume()
    {
        Volume volume = NewVolume();
        volume.MarkCreated("vol-ext");
        return volume;
    }

    private static Guid Waiting(Volume volume) => volume.Operation.PendingId!.Value;

    private static Guid Waiting(VolumeSnapshot snapshot) => snapshot.Operation.PendingId!.Value;

    private IEnumerable<string> Outcomes() => _activity.Events.Where(e => e.OutcomeTenantId != null).Select(e => e.Action);

    // ---------- the volume's request report ----------

    [Fact]
    public async Task Done_OfTheCreate_StoresTheId_RecordsVolumeCreatedOnce_AndSaves()
    {
        Volume volume = Given(NewVolume());

        await EndHandler().HandleAsync(new EndVolumeRequestCommand(volume.Id, Waiting(volume), "Done", "vol-ext"));

        Assert.Null(volume.Operation.PendingId);
        Assert.Equal(("vol-ext", VolumeStatus.Available), (volume.ExternalId, volume.Status));
        Assert.Equal(["volume.created"], Outcomes());
        _repo.Verify(r => r.SaveAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Done_OfTheCreate_WithoutTheVolumeName_Is422_AndSavesNothing()
    {
        Volume volume = Given(NewVolume());

        var refused = await Assert.ThrowsAsync<BusinessException>(() =>
            EndHandler().HandleAsync(new EndVolumeRequestCommand(volume.Id, Waiting(volume), "Done")));

        Assert.Equal("INVALID_STATUS", refused.Code);
        Assert.Equal(VolumeStatus.Creating, volume.Status);
        Assert.NotNull(volume.Operation.PendingId);
        _repo.Verify(r => r.SaveAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Done_OfAnAttach_ResolvesTheInstanceToTheVm_AndRecordsVolumeAttachedOnce()
    {
        Volume volume = Given(RestingVolume());
        VolumeAttachProviderIds attach = volume.Attach(_vm);

        await EndHandler().HandleAsync(new EndVolumeRequestCommand(volume.Id, attach.OperationId, "Done", AttachedTo: ["vm-ext"]));

        Assert.Equal(VolumeStatus.Available, volume.Status);
        Assert.Null(volume.Operation.PendingId);
        Assert.Equal(_vm.Id, volume.CurrentAttachedVmId);
        Assert.False(volume.CurrentAttachedElsewhere);
        Assert.Equal(["volume.attached"], Outcomes());
    }

    // A report without the instance list did not read the volume on Incus: the attachment last seen stays.
    [Fact]
    public async Task Failed_WithoutTheInstanceList_LeavesTheAttachmentAsLastSeen()
    {
        Volume volume = Given(RestingVolume());
        VolumeAttachProviderIds attach = volume.Attach(_vm);
        await EndHandler().HandleAsync(new EndVolumeRequestCommand(volume.Id, attach.OperationId, "Done", AttachedTo: ["vm-ext"]));
        OperationTicket detach = volume.Detach();

        await EndHandler().HandleAsync(new EndVolumeRequestCommand(volume.Id, detach.OperationId, "Failed",
            Reason: "The volume request could not be completed.", Detail: "raw"));

        Assert.Equal(VolumeStatus.Available, volume.Status);
        Assert.Null(volume.Operation.PendingId);
        Assert.Equal(_vm.Id, volume.CurrentAttachedVmId);
        Assert.False(volume.CurrentAttachedElsewhere);
    }

    [Theory]
    [InlineData("Attach", "volume.attach_failed")]
    [InlineData("Detach", "volume.detach_failed")]
    [InlineData("Delete", "volume.delete_failed")]
    public async Task Failed_PutsDesiredBack_RecordsTheReason_AndTheFailedOutcomeOnce(string kind, string outcome)
    {
        Volume volume = Given(RestingVolume());
        Guid requestId;
        List<string> attachedTo = [];
        switch (kind)
        {
            case "Attach":
                requestId = volume.Attach(_vm).OperationId;
                break;
            case "Detach":
                VolumeAttachProviderIds attach = volume.Attach(_vm);
                volume.AttachSucceeded(attach.OperationId, null, true, _vm.Id, false);
                requestId = volume.Detach().OperationId;
                attachedTo = ["vm-ext"];
                break;
            default:
                requestId = volume.Delete()!.OperationId;
                break;
        }

        await EndHandler().HandleAsync(new EndVolumeRequestCommand(volume.Id, requestId, "Failed",
            AttachedTo: kind == "Delete" ? null : attachedTo, Reason: "The volume request could not be completed.", Detail: "raw"));

        Assert.Equal(VolumeStatus.Available, volume.Status);
        Assert.Null(volume.Operation.PendingId);
        Assert.Equal(volume.CurrentAttachedVmId, volume.DesiredAttachedVmId);
        Assert.Equal(("The volume request could not be completed.", "raw"), (volume.Operation.FailureReason, volume.Operation.FailureDetail));
        Assert.Equal([outcome], Outcomes());
    }

    [Fact]
    public async Task Failed_OfTheCreate_RecordsTheReason_KeepsAVolumeIncusMade_AndRecordsNoOutcome()
    {
        Volume volume = Given(NewVolume());

        await EndHandler().HandleAsync(new EndVolumeRequestCommand(volume.Id, Waiting(volume), "Failed", "vol-ext",
            Reason: "Volume creation failed.", Detail: "raw"));

        Assert.Equal(("vol-ext", VolumeStatus.Available), (volume.ExternalId, volume.Status));
        Assert.Equal("Volume creation failed.", volume.Operation.FailureReason);
        Assert.Empty(Outcomes());
    }

    [Fact]
    public async Task Done_OfTheDelete_RecordsVolumeDeleted_AndTheSnapshotsWhoseDeleteWaited()
    {
        Volume volume = Given(RestingVolume());
        VolumeSnapshot leaving = volume.WithSnapshot("leaving");
        volume.MarkSnapshotCreated(leaving.Id, "vol-ext/s1");
        volume.DeleteSnapshot(leaving.Id);
        VolumeSnapshot gone = volume.WithSnapshot("gone");
        volume.MarkSnapshotCreated(gone.Id, "vol-ext/s2");
        volume.SnapshotDeleteSucceeded(gone.Id, volume.DeleteSnapshot(gone.Id)!.OperationId);
        VolumeSnapshot lost = volume.WithSnapshot("lost");
        volume.MarkSnapshotCreated(lost.Id, "vol-ext/s3");
        volume.MarkSnapshotNotFound(lost.Id);
        OperationTicket delete = volume.Delete()!;

        await EndHandler().HandleAsync(new EndVolumeRequestCommand(volume.Id, delete.OperationId, "Done"));

        Assert.Equal(VolumeStatus.Deleted, volume.Status);
        Assert.Equal(["volume.deleted", "volume_snapshot.deleted"], Outcomes().OrderBy(a => a, StringComparer.Ordinal));
        Assert.Equal(leaving.Id, _activity.Events.Single(e => e.Action == "volume_snapshot.deleted").ResourceId);
    }

    [Fact]
    public async Task AReport_NamingAnotherRequest_IsIgnored_RecordsNothing_AndSavesNothing()
    {
        Volume volume = Given(RestingVolume());
        OperationTicket delete = volume.Delete()!;

        await EndHandler().HandleAsync(new EndVolumeRequestCommand(volume.Id, Guid.NewGuid(), "Failed", Reason: "stale"));

        Assert.Equal(VolumeStatus.Deleting, volume.Status);
        Assert.Equal(delete.OperationId, volume.Operation.PendingId);
        Assert.Null(volume.Operation.FailureReason);
        Assert.Empty(_activity.Events);
        _repo.Verify(r => r.SaveAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    // The status the volume waits in says which operation a report can end: when nothing waits (the volume rests, gave
    // up its create, or is gone) there is none, so the report is ignored, records nothing and saves nothing.
    [Theory]
    [InlineData("Rests", "Done")]
    [InlineData("Rests", "Failed")]
    [InlineData("CreateFailed", "Done")]
    [InlineData("CreateFailed", "Failed")]
    [InlineData("Deleted", "Done")]
    [InlineData("Deleted", "Failed")]
    public async Task AReport_WhenNothingWaits_IsIgnored_RecordsNothing_AndSavesNothing(string situation, string outcome)
    {
        Volume volume;
        if (situation == "CreateFailed")
        {
            volume = NewVolume();
            volume.CreateFailed(Waiting(volume), null, false, null, false, "Volume creation failed.", "raw");
        }
        else
        {
            volume = RestingVolume();
            if (situation == "Deleted")
            {
                volume.DeleteSucceeded(volume.Delete()!.OperationId, null, false, null, false);
            }
        }
        Given(volume);
        VolumeStatus status = volume.Status;
        string? externalId = volume.ExternalId;
        string? failureReason = volume.Operation.FailureReason;

        await EndHandler().HandleAsync(new EndVolumeRequestCommand(volume.Id, Guid.NewGuid(), outcome, "vol-late",
            AttachedTo: ["vm-ext"], Reason: "stale", Detail: "stale"));

        Assert.Equal(status, volume.Status);
        Assert.Equal(externalId, volume.ExternalId);
        Assert.Equal(failureReason, volume.Operation.FailureReason);
        Assert.Null(volume.Operation.PendingId);
        Assert.Null(volume.CurrentAttachedVmId);
        Assert.Empty(_activity.Events);
        _repo.Verify(r => r.SaveAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AReport_WithAnUnknownOutcome_Is400_BeforeTheVolumeIsRead()
    {
        var refused = await Assert.ThrowsAsync<BadRequestException>(() =>
            EndHandler().HandleAsync(new EndVolumeRequestCommand(Guid.NewGuid(), Guid.NewGuid(), "Maybe")));

        Assert.Equal("INVALID_STATUS", refused.Code);
        _repo.Verify(r => r.FindByIdForUpdateAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AReport_ForAVolumeThatIsNotThere_Is404()
    {
        var refused = await Assert.ThrowsAsync<NotFoundException>(() =>
            EndHandler().HandleAsync(new EndVolumeRequestCommand(Guid.NewGuid(), Guid.NewGuid(), "Done")));

        Assert.Equal("VOLUME_NOT_FOUND", refused.Code);
    }

    // ---------- the reconciler's observation ----------

    [Fact]
    public async Task AnObservation_WritesCurrent_AndLeavesADeleteWaiting()
    {
        Volume volume = Given(RestingVolume());
        OperationTicket delete = volume.Delete()!;

        await ObserveHandler().HandleAsync(new ObserveVolumeCommand(volume.Id, false));

        // The delete keeps waiting; the observation is only what Incus was last seen to hold: not the volume.
        Assert.Equal(VolumeStatus.Deleting, volume.Status);
        Assert.Null(volume.InstanceStatus);
        Assert.Equal(delete.OperationId, volume.Operation.PendingId);
        Assert.Empty(_activity.Events);
    }

    [Fact]
    public async Task AnObservation_OfAnInstanceOutsideTitan_IsAttachedElsewhere_AndDrift()
    {
        Volume volume = Given(RestingVolume());

        await ObserveHandler().HandleAsync(new ObserveVolumeCommand(volume.Id, true, ["not-a-titan-vm"]));

        Assert.True(volume.CurrentAttachedElsewhere);
        Assert.Null(volume.CurrentAttachedVmId);
        Assert.Null(volume.DesiredAttachedVmId);
        Assert.Equal(VolumeStatus.Available, volume.Status);
        Assert.Null(volume.Operation.PendingId);
        Assert.Empty(_activity.Events);
    }

    // An observation never ends a request, even one showing what a waiting attach asked for: only the report carrying
    // the request's id does (titan-ops architecture/resource-lifecycle.md#invariants).
    [Fact]
    public async Task AnObservation_ReachingAWaitingAttach_LeavesItWaiting_AndTheLaterDone_EndsIt()
    {
        Volume volume = Given(RestingVolume());
        VolumeAttachProviderIds attach = volume.Attach(_vm);

        await ObserveHandler().HandleAsync(new ObserveVolumeCommand(volume.Id, true, ["vm-ext"]));

        Assert.Equal(_vm.Id, volume.CurrentAttachedVmId);
        Assert.Equal(VolumeStatus.Attaching, volume.Status);
        Assert.Equal(attach.OperationId, volume.Operation.PendingId);
        Assert.Empty(_activity.Events);

        await EndHandler().HandleAsync(new EndVolumeRequestCommand(volume.Id, attach.OperationId, "Done", AttachedTo: ["vm-ext"]));

        Assert.Equal(VolumeStatus.Available, volume.Status);
        Assert.Equal(["volume.attached"], Outcomes());
    }

    // ---------- a snapshot's request report and observation ----------

    [Fact]
    public async Task Done_OfASnapshotCreate_StoresTheId_AndRecordsVolumeSnapshotCreatedOnce()
    {
        Volume volume = Given(RestingVolume());
        VolumeSnapshot snapshot = volume.WithSnapshot("s1");

        await EndSnapshotHandler().HandleAsync(
            new EndVolumeSnapshotRequestCommand(volume.Id, snapshot.Id, Waiting(snapshot), "Done", "vol-ext/s1"));

        Assert.Equal(VolumeSnapshotStatus.Available, snapshot.Status);
        Assert.Null(snapshot.Operation.PendingId);
        Assert.Equal("vol-ext/s1", snapshot.ExternalId);
        Assert.Equal(["volume_snapshot.created"], Outcomes());
    }

    [Fact]
    public async Task Done_OfASnapshotCreate_WithoutTheSnapshotId_Is422_AndSavesNothing()
    {
        Volume volume = Given(RestingVolume());
        VolumeSnapshot snapshot = volume.WithSnapshot("s1");

        var refused = await Assert.ThrowsAsync<BusinessException>(() => EndSnapshotHandler().HandleAsync(
            new EndVolumeSnapshotRequestCommand(volume.Id, snapshot.Id, Waiting(snapshot), "Done")));

        Assert.Equal("INVALID_STATUS", refused.Code);
        Assert.Equal(VolumeSnapshotStatus.Creating, snapshot.Status);
        Assert.NotNull(snapshot.Operation.PendingId);
        _repo.Verify(r => r.SaveAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Failed_OfASnapshotDelete_GivesItBack_AndRecordsTheFailedOutcomeOnce()
    {
        Volume volume = Given(RestingVolume());
        VolumeSnapshot snapshot = volume.WithSnapshot("s1");
        volume.MarkSnapshotCreated(snapshot.Id, "vol-ext/s1");
        OperationTicket delete = volume.DeleteSnapshot(snapshot.Id)!;

        await EndSnapshotHandler().HandleAsync(
            new EndVolumeSnapshotRequestCommand(volume.Id, snapshot.Id, delete.OperationId, "Failed", Reason: "could not"));

        Assert.Equal(VolumeSnapshotStatus.Available, snapshot.Status);
        Assert.Null(snapshot.Operation.PendingId);
        Assert.Equal(["volume_snapshot.delete_failed"], Outcomes());
    }

    // Same for a snapshot: no waiting operation, no method to pick, nothing recorded or saved.
    [Theory]
    [InlineData("Rests", "Done")]
    [InlineData("Rests", "Failed")]
    [InlineData("CreateFailed", "Done")]
    [InlineData("CreateFailed", "Failed")]
    [InlineData("Deleted", "Done")]
    [InlineData("Deleted", "Failed")]
    public async Task ASnapshotReport_WhenNothingWaits_IsIgnored_RecordsNothing_AndSavesNothing(string situation, string outcome)
    {
        Volume volume = Given(RestingVolume());
        VolumeSnapshot snapshot = volume.WithSnapshot("s1");
        if (situation == "CreateFailed")
        {
            volume.SnapshotCreateFailed(snapshot.Id, Waiting(snapshot), null, "Snapshot creation failed.", "raw");
        }
        else
        {
            volume.MarkSnapshotCreated(snapshot.Id, "vol-ext/s1");
            if (situation == "Deleted")
            {
                volume.SnapshotDeleteSucceeded(snapshot.Id, volume.DeleteSnapshot(snapshot.Id)!.OperationId);
            }
        }
        VolumeSnapshotStatus status = snapshot.Status;
        string? externalId = snapshot.ExternalId;
        string? failureReason = snapshot.Operation.FailureReason;

        await EndSnapshotHandler().HandleAsync(new EndVolumeSnapshotRequestCommand(volume.Id, snapshot.Id, Guid.NewGuid(),
            outcome, "vol-ext/late", Reason: "stale", Detail: "stale"));

        Assert.Equal(status, snapshot.Status);
        Assert.Equal(externalId, snapshot.ExternalId);
        Assert.Equal(failureReason, snapshot.Operation.FailureReason);
        Assert.Null(snapshot.Operation.PendingId);
        Assert.Empty(_activity.Events);
        _repo.Verify(r => r.SaveAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ASnapshotReport_UnderAnotherVolume_Is404()
    {
        Volume volume = Given(RestingVolume());

        var refused = await Assert.ThrowsAsync<NotFoundException>(() => EndSnapshotHandler().HandleAsync(
            new EndVolumeSnapshotRequestCommand(volume.Id, Guid.NewGuid(), Guid.NewGuid(), "Done", "x")));

        Assert.Equal("VOLUME_SNAPSHOT_NOT_FOUND", refused.Code);
    }

    [Fact]
    public async Task ASnapshotObservation_WritesCurrent_AndLeavesTheCreateWaiting()
    {
        Volume volume = Given(RestingVolume());
        VolumeSnapshot snapshot = volume.WithSnapshot("s1");

        await ObserveSnapshotHandler().HandleAsync(new ObserveVolumeSnapshotCommand(volume.Id, snapshot.Id, false));

        Assert.Equal(VolumeSnapshotStatus.Creating, snapshot.Status);
        Assert.Null(snapshot.InstanceStatus);
        Assert.NotNull(snapshot.Operation.PendingId);
        _repo.Verify(r => r.SaveAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}
