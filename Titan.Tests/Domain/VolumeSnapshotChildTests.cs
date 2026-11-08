using Titan.Domain.Aggregates.Tenant;
using Titan.Domain.Aggregates.Volume;
using Titan.Domain.Exceptions;
using Titan.Domain.Values;

namespace Titan.Tests.Domain;

// A volume snapshot is the volume's child entity (titan-ops architecture/code-structure.md#child-entity): the volume
// counts its snapshots toward the per-volume cap and toward "may this volume be deleted", routes each report and
// observation to the snapshot it names, and takes every snapshot with it when its own delete is confirmed.
public class VolumeSnapshotChildTests
{
    private static readonly Titan.Domain.Aggregates.Tenant.Tenant TenantIdOwner = TestTenant.WithRoomToSpare();
    private static readonly Guid TenantId = TenantIdOwner.Id;

    private static Volume RestingVolume()
    {
        var volume = Volume.Create(TenantIdOwner.Id, "vol-1", "cluster-1", "offering-1", 50);
        volume.MarkCreated("vol11");
        return volume;
    }

    // The same five situations the repository count once measured (titan-ops features/volume/design.md#counting-and-billing):
    // creating and held by Incus count; a delete in flight, a snapshot Incus lost, a Deleted row and a failed create do not.
    private static Volume VolumeWithEverySnapshotSituation()
    {
        var volume = RestingVolume();
        volume.WithSnapshot("creating"); // counts
        volume.WithSnapshot("ready").MarkCreated("vol11/s1"); // counts
        var leaving = volume.WithSnapshot("leaving");
        leaving.MarkCreated("vol11/s2");
        leaving.Delete();
        var lost = volume.WithSnapshot("lost");
        lost.MarkCreated("vol11/s3");
        lost.MarkNotFound();
        var gone = volume.WithSnapshot("gone");
        gone.MarkCreated("vol11/s4");
        volume.SnapshotDeleteSucceeded(gone.Id, volume.DeleteSnapshot(gone.Id)!.OperationId);
        volume.WithSnapshot("failed").MarkRequestFailed("could not create", "raw");
        return volume;
    }

    [Fact]
    public void SnapshotQuotaUsage_CountsCreatingAndExisting_ButNotADeleteInFlight_NotFound_NorAFailedCreate()
    {
        var volume = VolumeWithEverySnapshotSituation();

        Assert.Equal(6, volume.Snapshots.Count);
        Assert.Equal(2, volume.SnapshotQuotaUsage);
    }

    [Fact]
    public void AddSnapshot_OneBelowTheCap_AddsASnapshotWaitingForItsCreate()
    {
        var volume = VolumeWithEverySnapshotSituation(); // usage 2

        VolumeSnapshot snapshot = volume.WithSnapshot("nightly");

        Assert.Contains(snapshot, volume.Snapshots);
        Assert.Equal(volume.Id, snapshot.VolumeId);
        Assert.Equal(TenantId, snapshot.TenantId);
        Assert.Equal(VolumeSnapshotStatus.Creating, snapshot.Status);
        Assert.NotNull(snapshot.Operation.PendingId);
        Assert.Equal(3, volume.SnapshotQuotaUsage);
    }

    [Fact]
    public void AddSnapshot_OnAVolumeNotResting_IsRefused()
    {
        var volume = Volume.Create(TenantIdOwner.Id, "vol-1", "cluster-1", "offering-1", 50); // Creating

        var ex = Assert.Throws<BusinessException>(() => volume.AddSnapshot(Guid.NewGuid(), "nightly"));

        Assert.Equal("VOLUME_INVALID_STATE", ex.Code);
        Assert.Empty(volume.Snapshots);
    }

    // The volume's delete Done took its snapshots with it (titan-ops features/volume/design.md#delete-refused-with-snapshots).
    // A snapshot's own waiting delete ends with it; which snapshots waited is read before the call, by VolumeReports.
    [Fact]
    public void DeleteDone_MarksEverySnapshotDeleted_AndEndsTheirWaitingOperations()
    {
        var volume = RestingVolume();
        var lost = volume.WithSnapshot("lost");
        lost.MarkCreated("vol11/s1");
        volume.MarkSnapshotNotFound(lost.Id);
        var leaving = volume.WithSnapshot("leaving");
        leaving.MarkCreated("vol11/s2");
        volume.DeleteSnapshot(leaving.Id);
        var alreadyGone = volume.WithSnapshot("gone");
        alreadyGone.MarkCreated("vol11/s3");
        volume.SnapshotDeleteSucceeded(alreadyGone.Id, volume.DeleteSnapshot(alreadyGone.Id)!.OperationId);
        OperationTicket delete = volume.Delete()!; // lost, leaving and gone do not count, so the delete is accepted

        bool ended = volume.DeleteSucceeded(delete.OperationId, null, false, null, false);

        Assert.True(ended);
        Assert.Equal(VolumeStatus.Deleted, volume.Status);
        Assert.All(volume.Snapshots, s => Assert.Equal(VolumeSnapshotStatus.Deleted, s.Status));
        Assert.All(volume.Snapshots, s => Assert.Null(s.Operation.PendingId));
    }

    [Fact]
    public void DeleteDone_WithNoSnapshots_EndsTheDelete()
    {
        var volume = RestingVolume();
        OperationTicket delete = volume.Delete()!;

        bool ended = volume.DeleteSucceeded(delete.OperationId, null, false, null, false);

        Assert.True(ended);
        Assert.Equal(VolumeStatus.Deleted, volume.Status);
    }

    // Deleted absorbs (titan-ops architecture/intent-and-observation.md#terminal-state-absorbs): the same Done again
    // ends nothing, so no snapshot's delete is recorded twice.
    [Fact]
    public void DeleteDone_Repeated_IsAbsorbed()
    {
        var volume = RestingVolume();
        var leaving = volume.WithSnapshot("leaving");
        leaving.MarkCreated("vol11/s2");
        volume.DeleteSnapshot(leaving.Id);
        OperationTicket delete = volume.Delete()!;
        volume.DeleteSucceeded(delete.OperationId, null, false, null, false);

        bool repeat = volume.DeleteSucceeded(delete.OperationId, null, false, null, false);

        Assert.False(repeat);
    }

    // A snapshot the volume does not have answers like a missing one.
    [Fact]
    public void ASnapshotOfAnotherVolume_IsNotFound()
    {
        var volume = RestingVolume();
        var other = RestingVolume();
        var foreign = other.WithSnapshot("theirs");
        volume.WithSnapshot("mine");

        Assert.DoesNotContain(volume.Snapshots, s => s.Id == foreign.Id);
        Assert.Equal("VOLUME_SNAPSHOT_NOT_FOUND", Assert.Throws<NotFoundException>(() => volume.DeleteSnapshot(foreign.Id)).Code);
        Assert.Equal("VOLUME_SNAPSHOT_NOT_FOUND", Assert.Throws<NotFoundException>(() => volume.ProviderIdForSnapshotRestore(foreign.Id)).Code);
        Guid foreignCreate = foreign.Operation.PendingId!.Value;
        Assert.Equal("VOLUME_SNAPSHOT_NOT_FOUND", Assert.Throws<NotFoundException>(
            () => volume.SnapshotCreateSucceeded(foreign.Id, foreignCreate, "vol11/s1")).Code);
        Assert.Equal("VOLUME_SNAPSHOT_NOT_FOUND", Assert.Throws<NotFoundException>(
            () => volume.SnapshotCreateFailed(foreign.Id, foreignCreate, null, "reason", "raw")).Code);
        Assert.Equal("VOLUME_SNAPSHOT_NOT_FOUND", Assert.Throws<NotFoundException>(
            () => volume.SnapshotDeleteSucceeded(foreign.Id, foreignCreate)).Code);
        Assert.Equal("VOLUME_SNAPSHOT_NOT_FOUND", Assert.Throws<NotFoundException>(
            () => volume.SnapshotDeleteFailed(foreign.Id, foreignCreate, "reason", "raw")).Code);
        Assert.Equal("VOLUME_SNAPSHOT_NOT_FOUND", Assert.Throws<NotFoundException>(() => volume.MarkSnapshotObserved(foreign.Id)).Code);
        Assert.Equal("VOLUME_SNAPSHOT_NOT_FOUND", Assert.Throws<NotFoundException>(() => volume.MarkSnapshotNotFound(foreign.Id)).Code);
    }

    // The root routes to the snapshot and returns what the snapshot returns.
    [Fact]
    public void SnapshotCalls_ReachTheSnapshotNamed_AndReturnItsAnswer()
    {
        var volume = RestingVolume();
        var snapshot = volume.WithSnapshot("s");
        var untouched = volume.WithSnapshot("u");
        Guid createRequestId = snapshot.Operation.PendingId!.Value;

        Assert.True(volume.SnapshotCreateSucceeded(snapshot.Id, createRequestId, "vol11/s1"));
        Assert.False(volume.SnapshotCreateSucceeded(snapshot.Id, createRequestId, "vol11/s1"));
        volume.MarkSnapshotObserved(snapshot.Id);
        Assert.Equal("vol11/s1", volume.ProviderIdForSnapshotRestore(snapshot.Id));

        OperationTicket refused = volume.DeleteSnapshot(snapshot.Id)!;
        Assert.Equal("vol11/s1", refused.ProviderId);
        Assert.Equal(VolumeSnapshotStatus.Deleting, snapshot.Status);
        Assert.True(volume.SnapshotDeleteFailed(snapshot.Id, refused.OperationId, "refused", "raw"));
        Assert.Equal(VolumeSnapshotStatus.Available, snapshot.Status);
        Assert.Null(snapshot.Operation.PendingId);
        Assert.Equal("refused", snapshot.Operation.FailureReason);

        volume.MarkSnapshotNotFound(snapshot.Id);
        Assert.Equal(VolumeSnapshotStatus.NotFound, snapshot.Status);
        OperationTicket delete = volume.DeleteSnapshot(snapshot.Id)!;
        Assert.True(volume.SnapshotDeleteSucceeded(snapshot.Id, delete.OperationId));
        Assert.Equal(VolumeSnapshotStatus.Deleted, snapshot.Status);

        Assert.Equal(VolumeSnapshotStatus.Creating, untouched.Status);
        Assert.Null(untouched.ExternalId);
    }

    // The create's give-up goes through the root too: with the id it leaves a snapshot on Incus, without it a failed one.
    [Fact]
    public void SnapshotCreateFailed_ReachesTheSnapshotNamed_AndLeavesItFailedOrAvailableByTheId()
    {
        var volume = RestingVolume();
        var withoutId = volume.WithSnapshot("without");
        var withId = volume.WithSnapshot("with");

        Assert.True(volume.SnapshotCreateFailed(withoutId.Id, withoutId.Operation.PendingId!.Value, null, "reason", "raw"));
        Assert.True(volume.SnapshotCreateFailed(withId.Id, withId.Operation.PendingId!.Value, "vol11/s2", "reason", "raw"));

        Assert.Equal(VolumeSnapshotStatus.Failed, withoutId.Status);
        Assert.Equal(VolumeSnapshotStatus.Available, withId.Status);
        Assert.Equal("vol11/s2", withId.ExternalId);
        Assert.Equal(1, volume.SnapshotQuotaUsage);
    }

    // Each of the four reports ends only the operation its snapshot waits in: the other pair is ignored even with the
    // right id, and writes nothing.
    [Fact]
    public void SnapshotReports_ForTheOtherOperationThanTheOneWaiting_AreIgnored()
    {
        var volume = RestingVolume();
        var creating = volume.WithSnapshot("creating");
        var deleting = volume.WithSnapshot("deleting");
        deleting.MarkCreated("vol11/s1");
        OperationTicket delete = volume.DeleteSnapshot(deleting.Id)!;
        Guid createId = creating.Operation.PendingId!.Value;

        Assert.False(volume.SnapshotDeleteSucceeded(creating.Id, createId));
        Assert.False(volume.SnapshotDeleteFailed(creating.Id, createId, "late", "late"));
        Assert.False(volume.SnapshotCreateSucceeded(deleting.Id, delete.OperationId, "vol11/s9"));
        Assert.False(volume.SnapshotCreateFailed(deleting.Id, delete.OperationId, "vol11/s9", "late", "late"));

        Assert.Equal(VolumeSnapshotStatus.Creating, creating.Status);
        Assert.Equal(createId, creating.Operation.PendingId);
        Assert.Equal(VolumeSnapshotStatus.Deleting, deleting.Status);
        Assert.Equal(delete.OperationId, deleting.Operation.PendingId);
        Assert.Equal("vol11/s1", deleting.ExternalId);
        Assert.Null(creating.Operation.FailureReason);
        Assert.Null(deleting.Operation.FailureReason);
    }

    [Fact]
    public void RequestSnapshotDelete_OfASnapshotWhoseCreateFailed_DeletesItAtOnce_WithNoTicket()
    {
        var volume = RestingVolume();
        var failed = volume.WithSnapshot("failed");
        volume.SnapshotCreateFailed(failed.Id, failed.Operation.PendingId!.Value, null, "reason", "detail");

        Assert.Null(volume.DeleteSnapshot(failed.Id));

        Assert.Equal(VolumeSnapshotStatus.Deleted, failed.Status);
        Assert.Null(failed.Operation.PendingId);
        Assert.Equal(0, volume.SnapshotQuotaUsage);
    }
}
