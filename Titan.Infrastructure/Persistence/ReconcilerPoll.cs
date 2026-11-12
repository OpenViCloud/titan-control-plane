namespace Titan.Infrastructure.Persistence;

/// When a reconciler last asked for its list, keyed "{kind}/{clusterId}", so every control plane replica's
/// /healthz/reconcilers reads the same polls. Not a domain aggregate.
public class ReconcilerPoll
{
    public string Key { get; set; } = string.Empty;
    public DateTimeOffset PolledAt { get; set; }
}
