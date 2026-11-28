using Microsoft.EntityFrameworkCore;
using Titan.Domain.Aggregates.Function;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.Repositories;

namespace Titan.Tests.Infrastructure;

// Every function query is the SQL twin of a Domain bool (titan-ops architecture/domain-methods.md#plain-code): the tenant
// list and the quota (OccupiesQuota, !IsDeleted), the reconciler's set (IsReconcilable) and the cleanup's
// (VersionsToClean). Every shape goes through each query and is compared with the bool. One row per shape.
//
// Silent about: Postgres-only behaviour (InMemory runs the same LINQ, not the SQL EF makes of it) and the row lock;
// FunctionRequestsDbTests holds those.
public class FunctionRepositoryStateTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private const string Cluster = TestFunctions.Cluster;

    private static TitanDbContext NewDb() =>
        new(new DbContextOptionsBuilder<TitanDbContext>()
            .UseInMemoryDatabase($"function-state-{Guid.NewGuid()}")
            .Options);

    private static Dictionary<string, Function> EveryShape()
    {
        Function Make(string name) => TestFunctions.AFunction(Tenant, name: name);

        Function inactive = Make("inactive");
        inactive.WithVersion();
        Function deploying = Make("deploying");
        deploying.Deploy(deploying.WithVersion().Id);
        Function active = Make("active");
        active.Running();
        Function notFound = Make("not-found");
        notFound.Running();
        notFound.MarkNotFound();
        Function deleting = Make("deleting");
        deleting.Running();
        deleting.Delete();
        Function deleted = Make("deleted");
        deleted.Running();
        deleted.DeleteSucceeded(deleted.Delete()!.OperationId);
        Function deletedAtOnce = Make("deleted-at-once");
        deletedAtOnce.WithVersion();
        deletedAtOnce.Delete();
        Function failedButNamed = Make("failed-but-named");
        failedButNamed.DeployFailed(failedButNamed.Deploy(failedButNamed.WithVersion().Id), "fn-made", "not ready", null);
        Function discarding = Make("discarding");
        for (int i = 0; i < Function.KeptVersions + 1; i++) discarding.WithVersion();
        Function otherCluster = TestFunctions.AFunction(Tenant, clusterId: "fn-other", name: "other-cluster");
        otherCluster.Running();

        return new Dictionary<string, Function>
        {
            ["inactive"] = inactive, ["deploying"] = deploying, ["active"] = active, ["not-found"] = notFound,
            ["deleting"] = deleting, ["deleted"] = deleted, ["deleted-at-once"] = deletedAtOnce,
            ["failed-but-named"] = failedButNamed, ["discarding"] = discarding, ["other-cluster"] = otherCluster,
        };
    }

    [Fact]
    public async Task EveryQuery_AgreesWithTheDomainBools_ForEveryShape()
    {
        Dictionary<string, Function> shapes = EveryShape();
        await using TitanDbContext db = NewDb();
        db.Functions.AddRange(shapes.Values);
        await db.SaveChangesAsync();
        var repo = new FunctionRepository(db);

        string[] NamesOf(IEnumerable<Function> functions) => functions.Select(f => f.Name).Order(StringComparer.Ordinal).ToArray();
        string[] Expected(Func<Function, bool> rule) => NamesOf(shapes.Values.Where(rule));

        Assert.Equal(Expected(f => !f.IsDeleted), NamesOf(await repo.ListByTenantAsync(Tenant)));
        Assert.Equal(shapes.Values.Count(f => f.OccupiesQuota), await repo.CountActiveByTenantAsync(Tenant));
        Assert.Equal(Expected(f => f.ClusterId == Cluster && f.IsReconcilable), NamesOf(await repo.ListReconcilableInClusterAsync(Cluster)));
        Assert.Equal(Expected(f => f.VersionsToClean.Any()), NamesOf(await repo.ListWithCodeToCleanAsync()));

        // The shapes say something: each query leaves some out.
        Assert.Equal(["deleted", "deleted-at-once", "discarding"], NamesOf(await repo.ListWithCodeToCleanAsync()));
        Assert.Equal(["active", "deleting", "failed-but-named", "not-found"], NamesOf(await repo.ListReconcilableInClusterAsync(Cluster)));
        Assert.Equal(8, await repo.CountActiveByTenantAsync(Tenant));
    }

    [Fact]
    public async Task AFunction_RoundTrips_WithItsVersions_AndItsOperation()
    {
        Function function = TestFunctions.AFunction(Tenant);
        FunctionVersion version = function.WithVersion(handler: null, memoryMiB: 1024);
        function.Deploy(version.Id);
        await using TitanDbContext db = NewDb();
        db.Functions.Add(function);
        await db.SaveChangesAsync();

        // Untracked: materialised from the store, not handed back from the tracker.
        Function? loaded = await new FunctionRepository(db).FindByIdAsync(function.Id);

        Assert.NotNull(loaded);
        Assert.NotSame(function, loaded);
        Assert.Equal(FunctionStatus.Deploying, loaded.Status);
        Assert.Equal(function.Operation.PendingId, loaded.Operation.PendingId);
        Assert.Equal(version.Id, loaded.DeployingVersionId);
        FunctionVersion loadedVersion = Assert.Single(loaded.Versions);
        Assert.Null(loadedVersion.Handler);
        Assert.Equal(1024, loadedVersion.MemoryMiB);
        Assert.Equal(version.Image, loadedVersion.Image);
    }
}
