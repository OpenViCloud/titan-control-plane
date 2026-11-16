using Microsoft.EntityFrameworkCore;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.Sessions;

namespace Titan.Tests.Infrastructure.Sessions;

// A console ticket opens one console websocket, once, within 30 s (titan-ops features/vm/design.md#vm-console). The
// client makes the ticket; Titan keeps only its SHA-256. ConsoleTicketDbTests measures the single use on Postgres.
public class ConsoleTicketStoreTests
{
    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 16, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private const string Ticket = "ovct_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OtherTicket = "ovct_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private readonly ManualClock _clock = new();
    private readonly TitanDbContext _db = new(new DbContextOptionsBuilder<TitanDbContext>()
        .UseInMemoryDatabase($"console-tickets-{Guid.NewGuid()}").Options);
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _vm = Guid.NewGuid();

    private ConsoleTicketStore Store() => new(_db, _clock);

    [Theory]
    [InlineData(Ticket, true)]
    [InlineData("ovct_0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ-_abcde", true)]
    [InlineData("ovc_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", false)]
    [InlineData("ovct_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", false)]
    [InlineData("ovct_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", false)]
    [InlineData("ovct_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa=", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void AWellFormedTicket_IsOvctAndFortyThreeBase64UrlCharacters(string? ticket, bool wellFormed)
    {
        Assert.Equal(wellFormed, ConsoleTicketStore.IsWellFormed(ticket));
    }

    [Fact]
    public async Task AnIssuedTicket_KeepsOnlyItsHash_ForThirtySeconds()
    {
        await Store().IssueAsync(_tenant, _vm, "kc-user", Ticket, CancellationToken.None);

        ConsoleTicket row = await _db.ConsoleTickets.SingleAsync();
        Assert.Equal(ConsoleTicketStore.HashOf(Ticket), row.TokenHash);
        Assert.NotEqual(Ticket, row.TokenHash);
        Assert.Equal(64, row.TokenHash.Length);
        Assert.Equal(_tenant, row.TenantId);
        Assert.Equal(_vm, row.VmId);
        Assert.Equal("kc-user", row.CreatedBy);
        Assert.Equal(_clock.Now + TimeSpan.FromSeconds(30), row.ExpiresAt);
    }

    [Fact]
    public async Task ATicket_IsTakenOnce()
    {
        await Store().IssueAsync(_tenant, _vm, "kc-user", Ticket, CancellationToken.None);

        ConsoleTicket? first = await Store().TakeAsync(Ticket, CancellationToken.None);
        ConsoleTicket? second = await Store().TakeAsync(Ticket, CancellationToken.None);

        Assert.NotNull(first);
        Assert.Equal(_vm, first.VmId);
        Assert.Null(second);
        Assert.Empty(await _db.ConsoleTickets.ToListAsync());
    }

    [Fact]
    public async Task ATicketOlderThanThirtySeconds_IsNotTaken_AndIsGone()
    {
        await Store().IssueAsync(_tenant, _vm, "kc-user", Ticket, CancellationToken.None);
        _clock.Now += TimeSpan.FromSeconds(30);

        Assert.Null(await Store().TakeAsync(Ticket, CancellationToken.None));
        Assert.Empty(await _db.ConsoleTickets.ToListAsync());
    }

    [Fact]
    public async Task ATicketNeverIssued_IsNotTaken()
    {
        await Store().IssueAsync(_tenant, _vm, "kc-user", Ticket, CancellationToken.None);

        Assert.Null(await Store().TakeAsync(OtherTicket, CancellationToken.None));
        Assert.Single(await _db.ConsoleTickets.ToListAsync());
    }

    // Tickets nobody used would pile up; each issue clears the tenant's expired ones, as the session limiter does.
    [Fact]
    public async Task Issuing_DeletesTheTenantsExpiredTickets_AndNoOtherTenants()
    {
        Guid otherTenant = Guid.NewGuid();
        await Store().IssueAsync(_tenant, _vm, "kc-user", Ticket, CancellationToken.None);
        await Store().IssueAsync(otherTenant, Guid.NewGuid(), "kc-other", OtherTicket, CancellationToken.None);
        _clock.Now += TimeSpan.FromMinutes(1);

        await Store().IssueAsync(_tenant, _vm, "kc-user", "ovct_ccccccccccccccccccccccccccccccccccccccccccc", CancellationToken.None);

        List<ConsoleTicket> rows = await _db.ConsoleTickets.ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.DoesNotContain(rows, r => r.TokenHash == ConsoleTicketStore.HashOf(Ticket));
        Assert.Contains(rows, r => r.TenantId == otherTenant);
    }

    [Fact]
    public async Task TheSameTicketIssuedTwice_KeepsTheFirstRow()
    {
        await Store().IssueAsync(_tenant, _vm, "kc-user", Ticket, CancellationToken.None);
        await Store().IssueAsync(_tenant, Guid.NewGuid(), "kc-other", Ticket, CancellationToken.None);

        ConsoleTicket row = await _db.ConsoleTickets.SingleAsync();
        Assert.Equal(_vm, row.VmId);
        Assert.Equal("kc-user", row.CreatedBy);
    }
}
