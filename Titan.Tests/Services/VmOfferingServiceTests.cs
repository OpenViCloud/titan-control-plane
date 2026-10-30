using Microsoft.Extensions.Logging;
using Moq;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Catalog.VmOffering;
using Titan.Application.Commands.Catalog.VmOffering.Contracts;
using Titan.Application.Queries.Catalog.VmOffering;
using Titan.Application.Queries.Catalog.VmOffering.Contracts;
using Titan.Domain.Aggregates.VmOffering;
using Titan.Domain.Aggregates.AdminAuditLog;
using Titan.Domain.Exceptions;
using Titan.Domain.Repositories;

using Titan.Tests.Fakes;

namespace Titan.Tests.Services;

public class VmOfferingServiceTests
{
    // ── ListAsync (customer read) ─────────────────────────────────────

    // The portal renders this list as a size dropdown, so it must read smallest-first.
    // Ordering by Id would give l, m, s, xl — alphabetically correct, nonsense to a customer.
    [Fact]
    public async Task ListAsync_OrdersSmallestFirst_NotAlphabetically()
    {
        var svc = Make(Offering("xl", 8, 16384), Offering("s", 1, 2048),
                       Offering("l", 4, 8192), Offering("m", 2, 4096));

        var result = await svc.ListAsync();

        Assert.Equal(new[] { "s", "m", "l", "xl" }, result.Select(o => o.Id).ToArray());
    }

    // Sizing is the whole point of the offering (titan-ops features/catalog/design.md#vm-offering-sizing) — the DTO must carry the numbers
    // the worker sets on the instance, not just a display label.
    [Fact]
    public async Task ListAsync_ExposesSizingOnDto()
    {
        var svc = Make(Offering("m", 2, 4096));

        var dto = Assert.Single(await svc.ListAsync());

        Assert.Equal("m", dto.Id);
        Assert.Equal(1.0m, dto.Cpu);      // CPU = vCPU × 0.5 — 2:1 overcommit (titan-ops features/catalog/design.md#vm-offering-sizing)
        Assert.Equal(2, dto.Vcpu);
        Assert.Equal(4096, dto.MemoryMb);
    }

    [Fact]
    public async Task ListAsync_EmptyCatalog_ReturnsEmptyList()
    {
        Assert.Empty(await Make().ListAsync());
    }

    // ── Domain guards ──────────────────────────────────────────────────────

    // A zero/negative-sized offering would produce a VM the provider refuses to create, and the
    // failure would surface far away (in the worker) instead of here.
    [Theory]
    [InlineData(0, 2048)]
    [InlineData(-1, 2048)]
    [InlineData(2, 0)]
    [InlineData(2, -512)]
    public void Create_NonPositiveSizing_Throws(int vcpu, int memoryMb)
    {
        Assert.Throws<ArgumentException>(() =>
            VmOffering.Create("bad", "Bad", 1.0m, vcpu, memoryMb, DefaultPrice));
    }

    [Fact]
    public void Create_NonPositiveCpuUnits_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            VmOffering.Create("bad", "Bad", 0m, 2, 4096, DefaultPrice));
    }

    // ── UpdatePriceAsync (titan-ops architecture/money-and-wallet.md#price-belongs-to-offering) ───────────────────────────────────

    // titan-ops architecture/http-contract.md#commands-return-no-data: the change returns nothing, so this
    // reads it back the way the admin table does — through the list.
    [Fact]
    public async Task UpdatePrice_ReplacesPrice_VisibleOnReadBack()
    {
        var (svc, _, _) = Build(Offering("m", 2, 4096));

        await svc.UpdatePriceAsync("m", 400_000m);

        var dto = Assert.Single(await svc.ListAsync(), o => o.Id == "m");
        Assert.Equal(400_000m, dto.PricePerHour);
    }

    // The trail has to answer who, when, which tier, and from what to what. The first two come from the
    // audit row itself; ResourceKey and Detail carry the other two.
    [Fact]
    public async Task UpdatePrice_RecordsWhoChangedWhichTierFromWhatToWhat()
    {
        var (svc, _, audit) = Build(Offering("m", 2, 4096));

        await svc.UpdatePriceAsync("m", 400_000m);

        var entry = Assert.Single(audit.Staged);
        Assert.Equal(UpdateVmOfferingPriceCommandHandler.PriceChangeAction, entry.Action);
        Assert.Equal(UpdateVmOfferingPriceCommandHandler.PriceChangeResourceType, entry.ResourceType);
        Assert.Equal(Actor, entry.ActorKeycloakUserId);
        Assert.Equal("m", entry.ResourceKey);
        // A literal, invariant-formatted: `$"{decimal}"` takes its separator from the host culture, and
        // this row is persisted. Pinned literally so the test fails on any host if the formatting ever
        // goes back to being ambient.
        Assert.Equal("311.256 -> 400000", entry.Detail);
        Assert.NotEqual(default, entry.CreatedAt);
    }

    // A price applies to every tenant, so there is no owning tenant to record. A reader that treats
    // the empty value as a tenant would attribute the change to a tenant that does not exist.
    [Fact]
    public async Task UpdatePrice_AuditRowIsPlatformScoped_NotAttributedToATenant()
    {
        var (svc, _, audit) = Build(Offering("m", 2, 4096));

        await svc.UpdatePriceAsync("m", 400_000m);

        Assert.Equal(Guid.Empty, Assert.Single(audit.Staged).OwnerTenantId);
    }

    // The audit row and the new price commit together — one SaveAsync, on the repository that shares
    // the DbContext. A price that changed with no trace, or a trace of a change that did not happen,
    // are both worse than failing.
    [Fact]
    public async Task UpdatePrice_PersistsAuditRowAndPriceInOneSave()
    {
        var (svc, repo, audit) = Build(Offering("m", 2, 4096));

        await svc.UpdatePriceAsync("m", 400_000m);

        Assert.Equal(1, repo.SaveCount);
        Assert.Single(audit.Staged);
    }

    // Unknown tier is a 404, not a silently created row.
    [Fact]
    public async Task UpdatePrice_UnknownOffering_ThrowsNotFound()
    {
        var (svc, _, audit) = Build(Offering("m", 2, 4096));

        var ex = await Assert.ThrowsAsync<NotFoundException>(
            () => svc.UpdatePriceAsync("does-not-exist", 400_000m));

        Assert.Equal("VM_OFFERING_NOT_FOUND", ex.Code);
        Assert.Empty(audit.Staged);
    }

    // A rejected price leaves the tier alone AND leaves no audit row. The validator answers
    // 400 for this; here we prove the aggregate is a real backstop rather than a second opinion.
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task UpdatePrice_NonPositive_RejectsAndRecordsNothing(decimal price)
    {
        var (svc, repo, audit) = Build(Offering("m", 2, 4096));

        await Assert.ThrowsAsync<ArgumentException>(() => svc.UpdatePriceAsync("m", price));

        Assert.Empty(audit.Staged);
        Assert.Equal(0, repo.SaveCount);
    }

    // A customer can see what a tier costs before creating one
    // (titan-ops architecture/money-and-wallet.md#price-belongs-to-offering).
    [Fact]
    public async Task ListAsync_ExposesPriceToCustomers()
    {
        var svc = Make(Offering("m", 2, 4096));

        Assert.Equal(DefaultPrice, Assert.Single(await svc.ListAsync()).PricePerHour);
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private static OfferingFacade Make(params VmOffering[] seed) =>
        Build(seed).Service;

    private static (OfferingFacade Service, StubRepo Repo, FakeAdminAuditLogRepository Audit) Build(
        params VmOffering[] seed)
    {
        var repo = new StubRepo(seed);
        var audit = new FakeAdminAuditLogRepository();
        var currentUser = new Mock<ICurrentUser>();
        currentUser.Setup(u => u.KeycloakUserId).Returns(Actor);
        return (new OfferingFacade(
            new ListVmOfferingsQueryHandler(repo),
            new UpdateVmOfferingPriceCommandHandler(repo, audit, currentUser.Object,
                Mock.Of<ILogger<UpdateVmOfferingPriceCommandHandler>>())), repo, audit);
    }

    // Facade over the list query and the price command handler so the tests keep one call shape.
    private sealed class OfferingFacade(ListVmOfferingsQueryHandler list, UpdateVmOfferingPriceCommandHandler updatePrice)
    {
        public Task<List<VmOfferingQueryResult>> ListAsync(CancellationToken ct = default)
            => list.HandleAsync(new ListVmOfferingsQuery(), ct);

        public Task UpdatePriceAsync(string id, decimal pricePerHour, CancellationToken ct = default)
            => updatePrice.HandleAsync(new UpdateVmOfferingPriceCommand(id, pricePerHour), ct);
    }

    private const string Actor = "kc-admin-1";

    // CPU units follow the catalog rule CPU = vCPU × 0.5 so the fixtures stay honest.
    private static VmOffering Offering(string id, int vcpu, int memoryMb) =>
        VmOffering.Create(id, id.ToUpperInvariant(), vcpu * 0.5m, vcpu, memoryMb, DefaultPrice);

    // Any positive number — these tests are about ordering and DTO shape, not about the price table.
    private const decimal DefaultPrice = 311.256m;

    private sealed class StubRepo(params VmOffering[] seed) : IVmOfferingRepository
    {
        public Task<VmOffering?> FindByIdAsync(string id, CancellationToken ct = default) =>
            Task.FromResult(seed.FirstOrDefault(o => o.Id == id));

        public Task<List<VmOffering>> ListAllAsync(CancellationToken ct = default) =>
            Task.FromResult(seed.ToList());

        public int SaveCount { get; private set; }

        public Task SaveAsync(CancellationToken ct = default)
        {
            SaveCount++;
            return Task.CompletedTask;
        }
    }
}
