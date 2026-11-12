using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Titan.Infrastructure.Persistence;

namespace Titan.Infrastructure.Sessions;

// Open shell sessions per tenant, one row each in core.shell_sessions, so every control plane replica counts the same
// sessions (titan-ops features/vm/design.md#ssh-session).
public sealed class ShellSessionLimiter
{
    public const int MaxSessionsPerTenant = 5;
    /// How often an open session writes that it is still open.
    public static readonly TimeSpan SeenEvery = TimeSpan.FromSeconds(30);
    /// A row not seen for this long holds no place: its replica died with the session open, or could not write for that
    /// long. The next open that gets a place deletes it.
    public static readonly TimeSpan GoneAfter = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan ReleaseTimeout = TimeSpan.FromSeconds(10);

    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _clock;
    private readonly ILogger<ShellSessionLimiter> _logger;
    private readonly TimeSpan _seenEvery;

    public ShellSessionLimiter(IServiceScopeFactory scopes, TimeProvider clock, ILogger<ShellSessionLimiter> logger)
        : this(scopes, clock, logger, SeenEvery)
    {
    }

    internal ShellSessionLimiter(IServiceScopeFactory scopes, TimeProvider clock, ILogger<ShellSessionLimiter> logger,
        TimeSpan seenEvery)
    {
        _scopes = scopes;
        _clock = clock;
        _logger = logger;
        _seenEvery = seenEvery;
    }

    /// A place for one more session, or null when the tenant already has MaxSessionsPerTenant open.
    public async Task<IAsyncDisposable?> TryAcquireAsync(Guid tenantId, Guid vmId, CancellationToken ct)
    {
        await using AsyncServiceScope scope = _scopes.CreateAsyncScope();
        TitanDbContext db = scope.ServiceProvider.GetRequiredService<TitanDbContext>();
        // Replicas opening for one tenant take turns on its row, so none counts before another's insert commits; the
        // SaveChangesAsync below, or the scope's end on a refusal, ends the lock.
        await db.LockRowAsync($"SELECT id FROM core.tenants WHERE id = {tenantId} FOR UPDATE", ct);
        DateTimeOffset now = _clock.GetUtcNow();
        DateTimeOffset seenSince = now - GoneAfter;
        List<ShellSession> sessions = await db.ShellSessions.Where(s => s.TenantId == tenantId).ToListAsync(ct);
        int open = sessions.Count(s => s.SeenAt >= seenSince);
        if (open >= MaxSessionsPerTenant)
        {
            return null;
        }
        db.ShellSessions.RemoveRange(sessions.Where(s => s.SeenAt < seenSince));
        var session = new ShellSession { Id = Guid.NewGuid(), TenantId = tenantId, VmId = vmId, OpenedAt = now, SeenAt = now };
        db.ShellSessions.Add(session);
        await db.SaveChangesAsync(ct);
        return new Place(this, session.Id);
    }

    private async Task KeepSeenAsync(Guid sessionId, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_seenEvery, _clock, stop);
                await using AsyncServiceScope scope = _scopes.CreateAsyncScope();
                TitanDbContext db = scope.ServiceProvider.GetRequiredService<TitanDbContext>();
                ShellSession? session = await db.ShellSessions.FindAsync([sessionId], stop);
                if (session == null)
                {
                    return;
                }
                session.SeenAt = _clock.GetUtcNow();
                await db.SaveChangesAsync(stop);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                // A session not seen for GoneAfter stops holding its place; it keeps running.
                _logger.LogWarning(e, "SSH session {SessionId}: writing that it is still open failed", sessionId);
            }
        }
    }

    private async Task ReleaseAsync(Guid sessionId)
    {
        using var timeout = new CancellationTokenSource(ReleaseTimeout);
        try
        {
            await using AsyncServiceScope scope = _scopes.CreateAsyncScope();
            TitanDbContext db = scope.ServiceProvider.GetRequiredService<TitanDbContext>();
            ShellSession? session = await db.ShellSessions.FindAsync([sessionId], timeout.Token);
            if (session != null)
            {
                db.ShellSessions.Remove(session);
                await db.SaveChangesAsync(timeout.Token);
            }
        }
        catch (Exception e)
        {
            // The row then ages out after GoneAfter.
            _logger.LogWarning(e, "SSH session {SessionId}: deleting its row failed", sessionId);
        }
    }

    private sealed class Place : IAsyncDisposable
    {
        private readonly ShellSessionLimiter _limiter;
        private readonly Guid _sessionId;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _keepSeen;
        private int _released;

        public Place(ShellSessionLimiter limiter, Guid sessionId)
        {
            _limiter = limiter;
            _sessionId = sessionId;
            _keepSeen = limiter.KeepSeenAsync(sessionId, _stop.Token);
        }

        public async ValueTask DisposeAsync()
        {
            bool firstRelease = Interlocked.Exchange(ref _released, 1) == 0;
            if (!firstRelease)
            {
                return;
            }
            await _stop.CancelAsync();
            await _keepSeen;
            _stop.Dispose();
            await _limiter.ReleaseAsync(_sessionId);
        }
    }
}
