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
        var svc = Make(Offering("standard", "Standard SSD"));

        var dto = Assert.Single(await svc.ListAsync());

        Assert.Equal("standard", dto.Id);
        Assert.Equal("Standard SSD", dto.Name);
        Assert.Equal(DefaultPrice, dto.PricePerGbPerHour);
    }

    [Fact]
    public async Task ListAsync_OrdersByName()
    {
        var svc = Make(Offering("b", "Bravo"), Offering("a", "Alpha"));

        var result = await svc.ListAsync();

        Assert.Equal(new[] { "Alpha", "Bravo" }, result.Select(o => o.Name).ToArray());
    }

    // Facade over ListVolumeOfferingsQueryHandler so the tests keep one call shape.
    private static VolumeOfferingFacade Make(params VolumeOffering[] seed) => new(new ListVolumeOfferingsQueryHandler(new StubRepo(seed)));

    private sealed class VolumeOfferingFacade(ListVolumeOfferingsQueryHandler handler)
    {
        public Task<List<VolumeOfferingQueryResult>> ListAsync(CancellationToken ct = default)
            => handler.HandleAsync(new ListVolumeOfferingsQuery(), ct);
    }

    private static VolumeOffering Offering(string id, string name) =>
        VolumeOffering.Create(id, name, DefaultPrice);

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
