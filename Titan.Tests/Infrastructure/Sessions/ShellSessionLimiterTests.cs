using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.Sessions;

namespace Titan.Tests.Infrastructure.Sessions;

// At most five shell sessions per tenant at once, counted in core.ssh_sessions so every control plane replica sees the
// same count (titan-ops features/vm/design.md#ssh-session). ShellSessionLimiterDbTests measures the lock on Postgres.
public class ShellSessionLimiterTests
{
    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 12, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly ManualClock _clock = new();
    private readonly ServiceProvider _services;

    public ShellSessionLimiterTests()
    {
        string database = $"ssh-sessions-{Guid.NewGuid()}";
        var services = new ServiceCollection();
        services.AddDbContext<TitanDbContext>(o => o.UseInMemoryDatabase(database));
        _services = services.BuildServiceProvider();
    }

    private ShellSessionLimiter NewLimiter() =>
        new(_services.GetRequiredService<IServiceScopeFactory>(), _clock, NullLogger<ShellSessionLimiter>.Instance);

    private static async Task<List<IAsyncDisposable?>> OpenAsync(ShellSessionLimiter limiter, Guid tenant, int count)
    {
        var places = new List<IAsyncDisposable?>();
        for (int i = 0; i < count; i++)
        {
            places.Add(await limiter.TryAcquireAsync(tenant, Guid.NewGuid(), CancellationToken.None));
        }
        return places;
    }

    private async Task<int> RowsAsync(Guid tenant)
    {
        using IServiceScope scope = _services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<TitanDbContext>().ShellSessions.CountAsync(s => s.TenantId == tenant);
    }

    [Fact]
    public async Task FiveSessions_AreTaken_AndTheSixthIsRefused()
    {
        ShellSessionLimiter limiter = NewLimiter();
        Guid tenant = Guid.NewGuid();

        List<IAsyncDisposable?> places = await OpenAsync(limiter, tenant, 5);

        Assert.All(places, Assert.NotNull);
        Assert.Null(await limiter.TryAcquireAsync(tenant, Guid.NewGuid(), CancellationToken.None));
        Assert.Equal(5, await RowsAsync(tenant));
    }

    [Fact]
    public async Task ClosingASession_FreesItsPlace()
    {
        ShellSessionLimiter limiter = NewLimiter();
        Guid tenant = Guid.NewGuid();
        List<IAsyncDisposable?> places = await OpenAsync(limiter, tenant, 5);

        await places[2]!.DisposeAsync();

        Assert.Equal(4, await RowsAsync(tenant));
        Assert.NotNull(await limiter.TryAcquireAsync(tenant, Guid.NewGuid(), CancellationToken.None));
        Assert.Null(await limiter.TryAcquireAsync(tenant, Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task ReleasingTwice_FreesOnePlaceOnly()
    {
        ShellSessionLimiter limiter = NewLimiter();
        Guid tenant = Guid.NewGuid();
        List<IAsyncDisposable?> places = await OpenAsync(limiter, tenant, 5);

        await places[0]!.DisposeAsync();
        await places[0]!.DisposeAsync();

        Assert.NotNull(await limiter.TryAcquireAsync(tenant, Guid.NewGuid(), CancellationToken.None));
        Assert.Null(await limiter.TryAcquireAsync(tenant, Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task Tenants_CountSeparately()
    {
        ShellSessionLimiter limiter = NewLimiter();
        await OpenAsync(limiter, Guid.NewGuid(), 5);

        Assert.NotNull(await limiter.TryAcquireAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None));
    }

    // Two replicas share the count: sessions opened through one limiter fill the tenant's places for the other.
    [Fact]
    public async Task SessionsOpenedOnAnotherReplica_CountToo()
    {
        Guid tenant = Guid.NewGuid();
        await OpenAsync(NewLimiter(), tenant, 3);

        List<IAsyncDisposable?> here = await OpenAsync(NewLimiter(), tenant, 3);

        Assert.NotNull(here[1]);
        Assert.Null(here[2]);
    }

    // A replica that died left its rows behind: a session not seen for longer than GoneAfter no longer holds a place,
    // and the next open deletes its row.
    [Fact]
    public async Task ASessionNotSeenForLongerThanGoneAfter_HoldsNoPlace_AndItsRowGoes()
    {
        Guid tenant = Guid.NewGuid();
        await OpenAsync(NewLimiter(), tenant, 5);

        _clock.Now += ShellSessionLimiter.GoneAfter + TimeSpan.FromSeconds(1);
        IAsyncDisposable? place = await NewLimiter().TryAcquireAsync(tenant, Guid.NewGuid(), CancellationToken.None);

        Assert.NotNull(place);
        Assert.Equal(1, await RowsAsync(tenant));
    }

    [Fact]
    public async Task ASessionSeenWithinGoneAfter_StillHoldsItsPlace()
    {
        Guid tenant = Guid.NewGuid();
        await OpenAsync(NewLimiter(), tenant, 5);

        _clock.Now += ShellSessionLimiter.GoneAfter - TimeSpan.FromSeconds(1);

        Assert.Null(await NewLimiter().TryAcquireAsync(tenant, Guid.NewGuid(), CancellationToken.None));
    }

    // While open, a session writes when it was last seen every SeenEvery, so another replica keeps counting it.
    [Fact]
    public async Task AnOpenSession_SaysItIsSeen_EverySeenEvery()
    {
        var limiter = new ShellSessionLimiter(_services.GetRequiredService<IServiceScopeFactory>(), _clock,
            NullLogger<ShellSessionLimiter>.Instance, seenEvery: TimeSpan.FromMilliseconds(20));
        Guid tenant = Guid.NewGuid();
        await using IAsyncDisposable? place = await limiter.TryAcquireAsync(tenant, Guid.NewGuid(), CancellationToken.None);
        DateTimeOffset later = _clock.Now + TimeSpan.FromMinutes(1);
        _clock.Now = later;

        DateTimeOffset seen = DateTimeOffset.MinValue;
        for (int i = 0; i < 100 && seen != later; i++)
        {
            await Task.Delay(20);
            using IServiceScope scope = _services.CreateScope();
            seen = await scope.ServiceProvider.GetRequiredService<TitanDbContext>().ShellSessions
                .Where(s => s.TenantId == tenant).Select(s => s.SeenAt).SingleAsync();
        }

        Assert.Equal(later, seen);
    }

    // An open session says it is alive more often than GoneAfter, so it never ages out while open.
    [Fact]
    public void SeenEvery_IsWellInsideGoneAfter()
    {
        Assert.True(ShellSessionLimiter.SeenEvery * 2 < ShellSessionLimiter.GoneAfter);
    }
}
