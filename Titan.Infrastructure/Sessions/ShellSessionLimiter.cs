namespace Titan.Infrastructure.Sessions;

// Open shell sessions per tenant, counted in this process (titan-ops features/vm/design.md#ssh-session): right while the
// control plane runs one replica; a second replica would need the count somewhere shared.
public sealed class ShellSessionLimiter
{
    public const int MaxSessionsPerTenant = 5;

    private readonly Dictionary<Guid, int> _open = new();
    private readonly object _lock = new();

    /// A place for one more session, or null when the tenant already has MaxSessionsPerTenant open.
    public IDisposable? TryAcquire(Guid tenantId)
    {
        lock (_lock)
        {
            int open = _open.GetValueOrDefault(tenantId);
            if (open >= MaxSessionsPerTenant)
            {
                return null;
            }
            _open[tenantId] = open + 1;
            return new Lease(this, tenantId);
        }
    }

    private void Release(Guid tenantId)
    {
        lock (_lock)
        {
            int open = _open.GetValueOrDefault(tenantId);
            if (open <= 1)
            {
                _open.Remove(tenantId);
                return;
            }
            _open[tenantId] = open - 1;
        }
    }

    private sealed class Lease(ShellSessionLimiter limiter, Guid tenantId) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            bool firstRelease = Interlocked.Exchange(ref _released, 1) == 0;
            if (firstRelease)
            {
                limiter.Release(tenantId);
            }
        }
    }
}
