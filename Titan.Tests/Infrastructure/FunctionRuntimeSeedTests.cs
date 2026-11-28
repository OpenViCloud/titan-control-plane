using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Titan.Domain.Aggregates.FunctionRuntime;
using Titan.Infrastructure.Persistence;

namespace Titan.Tests.Infrastructure;

// The runtime catalog exists only as seed rows, as the template catalog (titan-ops features/ovc-function/design.md#runtimes):
// which image a runtime runs and until when it is supported is decided here, and pinned here. The digests are the
// images titan-function-runtimes published; a digest changed here is a different runtime on every cluster.
public class FunctionRuntimeSeedTests
{
    private static IModel DesignTimeModel()
    {
        var options = new DbContextOptionsBuilder<TitanDbContext>()
            .UseInMemoryDatabase($"seed-{Guid.NewGuid()}")
            .Options;
        using var db = new TitanDbContext(options);
        // Seed data lives only in the design-time model, the one migrations are built from.
        return db.GetService<IDesignTimeModel>().Model;
    }

    private static List<IDictionary<string, object?>> SeedRows() =>
        DesignTimeModel().FindEntityType(typeof(FunctionRuntime))!.GetSeedData().ToList();

    [Fact]
    public void TheCatalog_IsTheThreeRuntimes_AllAmd64_AndListed_EachPinnedByDigest()
    {
        var rows = SeedRows().OrderBy(r => (string)r[nameof(FunctionRuntime.Name)]!, StringComparer.Ordinal).ToList();

        Assert.Equal(
            [
                ("go", "ghcr.io/openvicloud/titan-function-runtimes/go@sha256:f253fc8f929cefbfdf899cdf8804374c0eb78e23d650f552840ea9e334d116dd", (DateOnly?)null),
                ("nodejs26", "ghcr.io/openvicloud/titan-function-runtimes/nodejs26@sha256:9f06d060020e71de5e31fa6a8bfcd6b85cca5491282df2b28abeec4b1e386c3c", new DateOnly(2029, 4, 30)),
                ("python3.15", "ghcr.io/openvicloud/titan-function-runtimes/python3.15@sha256:5479dbe0d05ec82ba2ba0e6995a95619b231019639a7bf9aeb393a19d61c5a0b", new DateOnly(2031, 10, 31)),
            ],
            rows.Select(r => ((string)r[nameof(FunctionRuntime.Name)]!, (string)r[nameof(FunctionRuntime.Image)]!,
                (DateOnly?)r[nameof(FunctionRuntime.SupportEndsOn)])));
        Assert.All(rows, r => Assert.Equal("amd64", r[nameof(FunctionRuntime.Architecture)]));
        Assert.All(rows, r => Assert.Equal(true, r[nameof(FunctionRuntime.IsListed)]));
        Assert.Equal(3, rows.Select(r => (Guid)r[nameof(FunctionRuntime.Id)]!).Distinct().Count());
    }

    // Constant keys and timestamp: a NewGuid or UtcNow would churn the migration on every build.
    [Fact]
    public void TheSeed_IsTheSameOnEveryBuild()
    {
        Assert.Equal(
            SeedRows().Select(r => ((Guid)r[nameof(FunctionRuntime.Id)]!, (DateTimeOffset)r[nameof(FunctionRuntime.CreatedAt)]!)),
            SeedRows().Select(r => ((Guid)r[nameof(FunctionRuntime.Id)]!, (DateTimeOffset)r[nameof(FunctionRuntime.CreatedAt)]!)));
    }

    [Fact] // no function cluster rows: the first sync-all asks each runtime in every configured function cluster
    public void NoClusterRow_IsSeeded()
    {
        Assert.Empty(DesignTimeModel().FindEntityType(typeof(FunctionRuntimeCluster))!.GetSeedData());
    }
}
