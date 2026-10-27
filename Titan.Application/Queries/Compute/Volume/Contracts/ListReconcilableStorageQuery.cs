namespace Titan.Application.Queries.Compute.Volume.Contracts;

// The storage reconciler asks for the volumes and volume snapshots of one cluster it must observe, in one question.
public record ListReconcilableStorageQuery(string ClusterId);
