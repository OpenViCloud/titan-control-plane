using Moq;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.Vm;
using Titan.Application.Commands.Compute.Vm.Contracts;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Aggregates.Volume;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;
using Titan.Domain.Values;
using Titan.Tests.Domain;
using Titan.Tests.Fakes;
using Titan.Application.Exceptions;

namespace Titan.Tests.Services;

// The two request routes of titan-ops architecture/resource-lifecycle.md#callback-routes at the handler: a report ends the
// request it names and records its outcome once; an observation records the Incus word and ends nothing.
public class VmRequestHandlerTests
{
    private static readonly Titan.Domain.Aggregates.Tenant.Tenant Owner = TestTenant.WithRoomToSpare();

    private readonly Mock<IVmRepository> _repo = new();
    private readonly Mock<IVolumeRepository> _volumeRepo = new();
    private readonly FakeActivityRecorder _activity = new();

    public VmRequestHandlerTests()
    {
        _volumeRepo.Setup(r => r.ListAttachedToVmLockedAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(new List<Volume>());
    }

    private EndVmRequestCommandHandler EndHandler() => new(_repo.Object, _volumeRepo.Object, _activity);
    private ObserveVmCommandHandler ObserveHandler() => new(_repo.Object);

    private Vm Given(Vm vm)
    {
        _repo.Setup(r => r.FindByIdForUpdateAsync(vm.Id, It.IsAny<CancellationToken>())).ReturnsAsync(vm);
        return vm;
    }

    private static Vm NewVm() => Vm.Create(Owner.Id, Guid.NewGuid(), "vm-1", "m", "tpl-1", "cluster-a", 20);

    private static Vm RunningVm()
    {
        Vm vm = NewVm();
        vm.MarkRunning("42", "10.0.0.5");
        return vm;
    }

    private static VmObservationReport Seen(string status, string? ip = null) => new(status, ip);

    private IEnumerable<string> Outcomes() => _activity.Events.Where(e => e.OutcomeTenantId != null).Select(e => e.Action);

    // ---------- the report moves (CreateSucceeded … DeleteFailed): the report names the request ----------

    [Fact]
    public async Task Done_OfTheCreate_StoresTheId_RecordsVmCreatedOnce_AndSaves()
    {
        Vm vm = Given(NewVm());
        Guid createId = vm.PendingOperationId!.Value;

        await EndHandler().HandleAsync(new EndVmRequestCommand(vm.Id, createId, "Done", "42", Seen("Running", "10.0.0.5")));

        Assert.Null(vm.PendingOperationId);
        Assert.Equal("42", vm.ExternalId);
        Assert.Equal(VmStatus.Running, vm.ObservedState);
        Assert.Equal(VmStatus.Running, vm.Status);
        Assert.Equal(["vm.created"], Outcomes());
        Assert.Equal(Owner.Id, _activity.Events.Single().OutcomeTenantId);
        _repo.Verify(r => r.SaveAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Done_OfTheCreate_WithoutTheInstanceName_Is422_AndChangesNothing()
    {
        Vm vm = Given(NewVm());

        var ex = await Assert.ThrowsAsync<BusinessException>(() =>
            EndHandler().HandleAsync(new EndVmRequestCommand(vm.Id, vm.PendingOperationId!.Value, "Done")));

        Assert.Equal("INVALID_STATUS", ex.Code);
        Assert.NotNull(vm.PendingOperationId);
        Assert.Equal(VmStatus.Creating, vm.Status);
        _repo.Verify(r => r.SaveAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("Stop", "Stopped", "vm.stopped")]
    [InlineData("Start", "Running", "vm.started")]
    public async Task Done_OfAStopOrStart_EndsIt_AndRecordsTheOutcomeOnce(string kind, string seen, string outcome)
    {
        Vm vm = Given(RunningVm());
        if (kind == "Start") vm.MarkStopped();
        Guid operationId = Guid.NewGuid();
        if (kind == "Stop")
        {
            vm.Stop(operationId);
        }
        else
        {
            vm.Start(operationId);
        }

        await EndHandler().HandleAsync(new EndVmRequestCommand(vm.Id, operationId, "Done", null, Seen(seen)));
        await EndHandler().HandleAsync(new EndVmRequestCommand(vm.Id, operationId, "Done", null, Seen(seen)));

        Assert.Null(vm.PendingOperationId);
        Assert.Equal(seen, vm.ObservedState.ToString());
        Assert.Equal(seen, vm.Status.ToString());
        Assert.Equal([outcome], Outcomes());
        _repo.Verify(r => r.SaveAsync(It.IsAny<CancellationToken>()), Times.Once); // the repeat ends nothing and saves nothing
    }

    [Fact]
    public async Task Done_OfADelete_EndsTheVm_RecordsVmDeleted_TheSnapshotsWhoseDeleteWaited_AndReleasesItsVolumes()
    {
        Vm vm = Given(RunningVm());
        VmSnapshot leaving = vm.WithSnapshot("leaving");
        leaving.MarkCreated("42/s1");
        vm.DeleteSnapshot(leaving.Id, Guid.NewGuid());
        vm.WithSnapshot("resting").MarkCreated("42/s2");
        VmSnapshot gone = vm.WithSnapshot("gone");
        gone.MarkCreated("42/s3");
        Guid goneDelete = Guid.NewGuid();
        vm.DeleteSnapshot(gone.Id, goneDelete);
        vm.FinishSnapshotOperation(gone.Id, goneDelete, null);
        Volume volume = Volume.Create(Owner.Id, "data-1", vm.ClusterId, "std", 20);
        volume.MarkCreated("vol-x");
        volume.Attach(vm);
        volume.MarkAttached(vm.Id);
        _volumeRepo.Setup(r => r.ListAttachedToVmLockedAsync(vm.Id, It.IsAny<CancellationToken>())).ReturnsAsync([volume]);
        Guid delete = Guid.NewGuid();
        vm.Delete(delete);

        await EndHandler().HandleAsync(new EndVmRequestCommand(vm.Id, delete, "Done"));

        Assert.True(vm.IsDeleted);
        Assert.All(vm.Snapshots, s => Assert.Equal(CurrentExistence.Deleted, s.CurrentExistence));
        Assert.Equal(["vm.deleted", "vm_snapshot.deleted"], Outcomes().OrderBy(a => a, StringComparer.Ordinal));
        Assert.Equal(leaving.Id, _activity.Events.Single(e => e.Action == "vm_snapshot.deleted").ResourceId);
        Assert.Null(volume.CurrentAttachedVmId);
        Assert.Null(volume.DesiredAttachedVmId);
        Assert.Null(volume.Pending);
    }

    [Fact]
    public async Task Failed_OfADelete_LeavesItsVolumeAttached_WithNothingWaiting_AndANewDeleteCanTakeIt()
    {
        Vm vm = Given(RunningVm());
        Volume volume = Volume.Create(Owner.Id, "data-1", vm.ClusterId, "std", 20);
        volume.MarkCreated("vol-x");
        volume.Attach(vm);
        volume.MarkAttached(vm.Id);
        _volumeRepo.Setup(r => r.ListAttachedToVmLockedAsync(vm.Id, It.IsAny<CancellationToken>())).ReturnsAsync([volume]);
        Guid delete = Guid.NewGuid();
        vm.Delete(delete);
        volume.ReleaseFromVmDelete(vm.Id);

        await EndHandler().HandleAsync(new EndVmRequestCommand(vm.Id, delete, "Failed", Reason: "It failed."));

        Assert.Null(volume.Pending);
        Assert.Equal(vm.Id, volume.CurrentAttachedVmId);
        Assert.Equal(vm.Id, volume.DesiredAttachedVmId);
        Guid secondDelete = Guid.NewGuid();
        vm.Delete(secondDelete);
        Assert.Equal(VmStatus.Deleting, vm.Status);
        Assert.Equal(secondDelete, vm.PendingOperationId);
        volume.ReleaseFromVmDelete(vm.Id);
        Assert.Null(volume.DesiredAttachedVmId);
        Assert.Null(volume.Pending);
    }

    // A VM's delete is not a request of the volume (titan-ops architecture/resource-lifecycle.md#kinds): a detach the
    // customer asked for keeps its own request whichever way the VM's delete ends, and its own command reports it.
    [Theory]
    [InlineData("Done")]
    [InlineData("Failed")]
    public async Task ADeleteEnding_LeavesAVolumesOwnWaitingDetach_ToItsCommand(string outcome)
    {
        Vm vm = Given(RunningVm());
        Volume volume = Volume.Create(Owner.Id, "data-1", vm.ClusterId, "std", 20);
        volume.MarkCreated("vol-x");
        VolumeAttachProviderIds attach = volume.Attach(vm);
        volume.FinishOperation(attach.OperationId, null, true, vm.Id, false);
        OperationTicket detach = volume.Detach();
        _volumeRepo.Setup(r => r.ListAttachedToVmLockedAsync(vm.Id, It.IsAny<CancellationToken>())).ReturnsAsync([volume]);
        Guid delete = Guid.NewGuid();
        vm.Delete(delete);
        volume.ReleaseFromVmDelete(vm.Id);

        await EndHandler().HandleAsync(new EndVmRequestCommand(vm.Id, delete, outcome, Reason: "It failed."));

        Assert.Equal((VolumeOperationKind.Detach, detach.OperationId), (volume.Pending!.Kind, volume.Pending.Id));
        Assert.Null(volume.DesiredAttachedVmId);
        Assert.DoesNotContain(_activity.Events, e => e.Action.StartsWith("volume.", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Delete", "vm.delete_failed")]
    [InlineData("Stop", "vm.stop_failed")]
    public async Task Failed_OfADeleteOrStop_PutsTheStatusBackToTheLastIncusWord_RecordsTheReason_AndTheFailedOutcomeOnce(string kind, string outcome)
    {
        Vm vm = Given(RunningVm());
        Guid operationId = Guid.NewGuid();
        if (kind == "Delete")
        {
            vm.Delete(operationId);
        }
        else
        {
            vm.Stop(operationId);
        }

        await EndHandler().HandleAsync(new EndVmRequestCommand(vm.Id, operationId, "Failed", Reason: "The VM could not be changed.", Detail: "raw"));

        Assert.Null(vm.PendingOperationId);
        Assert.Equal(VmStatus.Running, vm.Status);
        Assert.Equal("The VM could not be changed.", vm.OperationFailureReason);
        Assert.Equal("raw", vm.OperationFailureDetail);
        Assert.Equal([outcome], Outcomes());
    }

    [Fact]
    public async Task Failed_OfTheCreate_RecordsTheReason_KeepsAnInstanceIncusMade_AndRecordsNoOutcome()
    {
        Vm vm = Given(NewVm());

        await EndHandler().HandleAsync(new EndVmRequestCommand(vm.Id, vm.PendingOperationId!.Value, "Failed", "45", null, "VM provisioning failed.", "raw"));

        Assert.Null(vm.PendingOperationId);
        Assert.Equal("45", vm.ExternalId);
        // Incus made the instance, so the VM is not Failed: no Incus word was read yet.
        Assert.True(vm.HasAnInstance);
        Assert.Equal(VmStatus.Unknown, vm.Status);
        Assert.Equal("VM provisioning failed.", vm.OperationFailureReason);
        Assert.Empty(_activity.Events);
        _repo.Verify(r => r.SaveAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AReport_NamingAnotherRequest_IsIgnored_RecordsNothing_AndSavesNothing()
    {
        Vm vm = Given(RunningVm());
        Guid stop = Guid.NewGuid();
        vm.Stop(stop);

        await EndHandler().HandleAsync(new EndVmRequestCommand(vm.Id, Guid.NewGuid(), "Done", null, Seen("Stopped")));
        await EndHandler().HandleAsync(new EndVmRequestCommand(vm.Id, Guid.NewGuid(), "Failed", Reason: "stale"));

        Assert.Equal(stop, vm.PendingOperationId!.Value);
        Assert.Equal(VmStatus.Stopping, vm.Status);
        Assert.Equal(VmStatus.Running, vm.ObservedState);
        Assert.Null(vm.OperationFailureReason);
        Assert.Empty(_activity.Events);
        _repo.Verify(r => r.SaveAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AReport_WhenNothingWaits_OrOnADeletedVm_IsIgnored()
    {
        Vm resting = Given(RunningVm());
        await EndHandler().HandleAsync(new EndVmRequestCommand(resting.Id, Guid.NewGuid(), "Done", null, Seen("Stopped")));
        Assert.Equal(VmStatus.Running, resting.ObservedState);
        Assert.Equal(VmStatus.Running, resting.Status);

        Vm deleted = Given(RunningVm());
        Guid delete = Guid.NewGuid();
        deleted.Delete(delete);
        deleted.MarkDeleted();
        await EndHandler().HandleAsync(new EndVmRequestCommand(deleted.Id, delete, "Done", null, Seen("Running")));
        Assert.True(deleted.IsDeleted);
        Assert.Null(deleted.ObservedState);

        Assert.Empty(_activity.Events);
        _repo.Verify(r => r.SaveAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("Finished", null)]
    [InlineData("Done", "Sleeping")]
    public async Task AMalformedReport_Is400_BeforeTheVmIsRead(string outcome, string? status)
    {
        Vm vm = Given(RunningVm());
        VmObservationReport? observed = status == null ? null : Seen(status);

        var ex = await Assert.ThrowsAsync<BadRequestException>(() =>
            EndHandler().HandleAsync(new EndVmRequestCommand(vm.Id, Guid.NewGuid(), outcome, null, observed)));

        Assert.Equal("INVALID_STATUS", ex.Code);
        _repo.Verify(r => r.FindByIdForUpdateAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AReport_ForAnUnknownVm_Is404()
    {
        var ex = await Assert.ThrowsAsync<NotFoundException>(() =>
            EndHandler().HandleAsync(new EndVmRequestCommand(Guid.NewGuid(), Guid.NewGuid(), "Done")));

        Assert.Equal("VM_NOT_FOUND", ex.Code);
    }

    // ---------- Observe: the Incus word; the status follows only when nothing waits ----------

    [Fact]
    public async Task AnObservation_RecordsTheIncusWord_AndLeavesTheCreateWaiting()
    {
        Vm vm = Given(NewVm());
        Guid createId = vm.PendingOperationId!.Value;

        await ObserveHandler().HandleAsync(new ObserveVmCommand(vm.Id, true, Seen("Running", "10.0.0.5")));

        Assert.Equal(createId, vm.PendingOperationId!.Value);
        Assert.Equal(VmStatus.Creating, vm.Status);
        Assert.Equal(VmStatus.Running, vm.ObservedState);
        Assert.Equal("10.0.0.5", vm.IpAddress);
        Assert.Empty(_activity.Events);
        _repo.Verify(r => r.SaveAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NotFound_LeavesAWaitingDeleteWaiting()
    {
        Vm vm = Given(RunningVm());
        Guid delete = Guid.NewGuid();
        vm.Delete(delete);

        await ObserveHandler().HandleAsync(new ObserveVmCommand(vm.Id, false));

        Assert.Equal(delete, vm.PendingOperationId!.Value);
        // The waiting delete keeps the status; the observation only forgets the Incus word.
        Assert.Equal(VmStatus.Deleting, vm.Status);
        Assert.Null(vm.ObservedState);
        Assert.Empty(_activity.Events);
    }

    // Seeing what a stop asked for is not its report: only the Done carrying the stop's id ends it
    // (titan-ops architecture/resource-lifecycle.md#invariants).
    [Fact]
    public async Task AnObservation_ReachingAWaitingStop_LeavesItWaiting_AndTheLaterDone_EndsIt()
    {
        Vm vm = Given(RunningVm());
        Guid stop = Guid.NewGuid();
        vm.Stop(stop);

        await ObserveHandler().HandleAsync(new ObserveVmCommand(vm.Id, true, Seen("Stopped")));

        Assert.Equal(stop, vm.PendingOperationId!.Value);
        Assert.Equal(VmStatus.Stopping, vm.Status);
        Assert.Equal(VmStatus.Stopped, vm.ObservedState);
        Assert.Empty(Outcomes());

        await EndHandler().HandleAsync(new EndVmRequestCommand(vm.Id, stop, "Done", null, Seen("Stopped")));

        Assert.Null(vm.PendingOperationId);
        Assert.Equal(VmStatus.Stopped, vm.Status);
        Assert.Equal(["vm.stopped"], Outcomes());
    }

    [Fact]
    public async Task AnObservation_OfAnExistingInstance_WithoutItsStatus_Is400()
    {
        Vm vm = Given(RunningVm());

        var ex = await Assert.ThrowsAsync<BadRequestException>(() =>
            ObserveHandler().HandleAsync(new ObserveVmCommand(vm.Id, true, null)));

        Assert.Equal("INVALID_STATUS", ex.Code);
        _repo.Verify(r => r.FindByIdForUpdateAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
