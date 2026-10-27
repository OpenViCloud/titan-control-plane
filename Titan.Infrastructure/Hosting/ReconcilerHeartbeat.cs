using System.Collections.Concurrent;

namespace Titan.Infrastructure.Hosting;

/// <summary>
/// Keyed "{kind}/{clusterId}" (e.g. "networks/vn-hcm-1"). A reconciler that died raises no error, its resources
/// just stop changing; this turns that silence into a /healthz/reconcilers signal. A list request proves the
/// loop reaches the control plane, not that the status reports that follow succeed.
/// </summary>
public sealed class ReconcilerHeartbeat
{
    public const string VmKind = "vms";
    public const string NetworkKind = "networks";
    public const string VolumeKind = "volumes";
    public const string FirewallKind = "firewall-rules";
    public const string VmSnapshotKind = "vm-snapshots";
    public const string TemplateKind = "templates";
    public const string LoadBalancerKind = "load-balancers";

    private readonly ConcurrentDictionary<string, ReconcilerPollState> _states = new();

    /// So the health check measures a boot grace window from here instead of alarming on a name it has never
    /// seen. First registration wins.
    public void Register(string key, DateTimeOffset at)
    {
        _states.TryAdd(key, new ReconcilerPollState(at, LastPollAt: null));
    }

    public void RecordPoll(string key, DateTimeOffset at)
    {
        _states.AddOrUpdate(
            key,
            _ => new ReconcilerPollState(RegisteredAt: at, LastPollAt: at),
            (_, previous) => previous with { LastPollAt = at });
    }

    public IReadOnlyDictionary<string, ReconcilerPollState> Snapshot()
    {
        return new Dictionary<string, ReconcilerPollState>(_states);
    }

    public static string KeyFor(string kind, string clusterId)
    {
        return $"{kind}/{clusterId}";
    }
}

/// <param name="RegisteredAt">When the reconciler was first expected — the reference for the boot grace window.</param>
/// <param name="LastPollAt">Last time it asked the control plane what to observe; null until the first time.</param>
public sealed record ReconcilerPollState(DateTimeOffset RegisteredAt, DateTimeOffset? LastPollAt);
