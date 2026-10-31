namespace Titan.Domain.Aggregates.UsageRecord;

public enum UsageResourceType
{
    Compute,
    Disk,
    // VM and volume snapshots, billed per snapshot at a flat rate: Titan does not track a snapshot's
    // size, so there is no GB quantity (UsageRecord.CreateSnapshot).
    Snapshot
}
