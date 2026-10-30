using Titan.Application.Queries.Catalog.VolumeOffering;
using Titan.Application.Queries.Catalog.VolumeOffering.Contracts;
using Titan.Domain.Aggregates.VolumeOffering;
using Titan.Domain.Repositories;

namespace Titan.Tests.Services;

public class VolumeOfferingServiceTests
{
    // The catalog is read from Titan's own DB, not live from the provider: a misconfigured provider
    // endpoint must not 500 the catalog, and storage placement is a worker concern.
    [Fact]
    public async Task ListAsync_ReturnsCatalogFromRepository()
    {
        var svc = Make(Offering("standard", "Standard SSD", sizeGb: 0, isCustomized: true));

        var dto = Assert.Single(await svc.ListAsync());

        Assert.Equal("standard", dto.Id);
        Assert.Equal("Standard SSD", dto.Name);
        Assert.Equal(0, dto.SizeGb);
        Assert.True(dto.IsCustomized);
    }

    // The portal renders fixed-size tiers as "Name — 50 GB" and customized ones as "Custom size",
    // so both flavours have to survive the round trip intact.
    [Fact]
    public async Task ListAsync_PreservesFixedSizeTiers()
    {
        var svc = Make(Offering("fixed-50", "Fixed 50", sizeGb: 50, isCustomized: false));

        var dto = Assert.Single(await svc.ListAsync());

        Assert.Equal(50, dto.SizeGb);
        Assert.False(dto.IsCustomized);
    }

    [Fact]
    public async Task ListAsync_OrdersByName()
    {
        var svc = Make(Offering("b", "Bravo", 0, true), Offering("a", "Alpha", 0, true));

        var result = await svc.ListAsync();

        Assert.Equal(new[] { "Alpha", "Bravo" }, result.Select(o => o.Name).ToArray());
    }

    // A fixed-size tier advertising 0 GB would let a customer create a zero-byte volume.
    [Fact]
    public void Create_FixedSizeTierWithoutSize_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            VolumeOffering.Create("bad", "Bad", sizeGb: 0, isCustomized: false, DefaultPrice));
    }

    // Facade over ListVolumeOfferingsQueryHandler so the tests keep one call shape.
    private static VolumeOfferingFacade Make(params VolumeOffering[] seed) => new(new ListVolumeOfferingsQueryHandler(new StubRepo(seed)));

    private sealed class VolumeOfferingFacade(ListVolumeOfferingsQueryHandler handler)
    {
        public Task<List<VolumeOfferingQueryResult>> ListAsync(CancellationToken ct = default)
            => handler.HandleAsync(new ListVolumeOfferingsQuery(), ct);
    }

    private static VolumeOffering Offering(string id, string name, int sizeGb, bool isCustomized) =>
        VolumeOffering.Create(id, name, sizeGb, isCustomized, DefaultPrice);

    // Any positive number — these tests are about ordering and DTO shape, not the price table.
    private const decimal DefaultPrice = 1.57914m;

    private sealed class StubRepo(params VolumeOffering[] seed) : IVolumeOfferingRepository
    {
        public Task<VolumeOffering?> FindByIdAsync(string id, CancellationToken ct = default) =>
            Task.FromResult(seed.FirstOrDefault(o => o.Id == id));

        public Task<List<VolumeOffering>> ListAllAsync(CancellationToken ct = default) =>
            Task.FromResult(seed.ToList());
    }
}
