using Titan.Infrastructure.Sessions;

namespace Titan.Tests.Infrastructure.Sessions;

// At most five shell sessions per tenant at once (titan-ops features/vm/design.md#ssh-session).
public class ShellSessionLimiterTests
{
    [Fact]
    public void FiveSessions_AreTaken_AndTheSixthIsRefused()
    {
        var limiter = new ShellSessionLimiter();
        Guid tenant = Guid.NewGuid();

        var leases = Enumerable.Range(0, 5).Select(_ => limiter.TryAcquire(tenant)).ToList();

        Assert.All(leases, Assert.NotNull);
        Assert.Null(limiter.TryAcquire(tenant));
    }

    [Fact]
    public void ClosingASession_FreesItsPlace()
    {
        var limiter = new ShellSessionLimiter();
        Guid tenant = Guid.NewGuid();
        var leases = Enumerable.Range(0, 5).Select(_ => limiter.TryAcquire(tenant)).ToList();

        leases[2]!.Dispose();

        Assert.NotNull(limiter.TryAcquire(tenant));
        Assert.Null(limiter.TryAcquire(tenant));
    }

    [Fact]
    public void ReleasingTwice_FreesOnePlaceOnly()
    {
        var limiter = new ShellSessionLimiter();
        Guid tenant = Guid.NewGuid();
        var leases = Enumerable.Range(0, 5).Select(_ => limiter.TryAcquire(tenant)).ToList();

        leases[0]!.Dispose();
        leases[0]!.Dispose();

        Assert.NotNull(limiter.TryAcquire(tenant));
        Assert.Null(limiter.TryAcquire(tenant));
    }

    [Fact]
    public void Tenants_CountSeparately()
    {
        var limiter = new ShellSessionLimiter();
        Guid full = Guid.NewGuid();
        for (int i = 0; i < 5; i++)
        {
            limiter.TryAcquire(full);
        }

        Assert.NotNull(limiter.TryAcquire(Guid.NewGuid()));
    }
}
