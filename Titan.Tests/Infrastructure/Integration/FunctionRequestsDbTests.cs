using Microsoft.EntityFrameworkCore;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.Function;
using Titan.Application.Commands.Compute.Function.Contracts;
using Titan.Domain.Aggregates.Function;
using Titan.Domain.Aggregates.ProviderCommand;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.ProviderCommands;
using Titan.Infrastructure.Repositories;

namespace Titan.Tests.Infrastructure.Integration;

/// What only Postgres can show of titan-ops architecture/resource-lifecycle.md for functions: the row lock that makes two
/// requests on one function take turns (§one-key-per-request), the stuck-request view joining a waiting request to its
/// command row (§lost-and-stale), a function with its versions round-tripping the real schema, and the report through the
/// real repository. Category=Integration — its own CI step.
[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public class FunctionRequestsDbTests(PostgresFixture pg)
{
    private readonly Guid _tenant = Guid.NewGuid();

    // A function whose version runs on the cluster: deployed, and the deploy's command reported Done.
    private async Task<Function> StoredRunningFunctionAsync()
    {
        Function function = TestFunctions.AFunction(_tenant, name: $"fn-{Guid.NewGuid():N}"[..12]);
        function.Running();
        await using TitanDbContext db = pg.NewDbContext();
        db.Functions.Add(function);
        await db.SaveChangesAsync();
        return function;
    }

    private async Task DeleteThroughTheHandlerAsync(Guid functionId)
    {
        await using TitanDbContext db = pg.NewDbContext();
        ICurrentUser caller = Callers.TenantMember(_tenant);
        var handler = new DeleteFunctionCommandHandler(new FunctionRepository(db), caller,
            new ProviderCommandQueue(new ProviderCommandRepository(db), TimeProvider.System));
        await handler.HandleAsync(new DeleteFunctionCommand(functionId));
    }

    private async Task<List<ProviderCommand>> CommandsOfAsync(Guid functionId)
    {
        await using TitanDbContext db = pg.NewDbContext();
        return await db.ProviderCommands.AsNoTracking().Where(c => c.ResourceId == functionId).ToListAsync();
    }

    // The second waits on the function's row lock, then sees the first's delete waiting and is a repeat: one request,
    // one command row, and neither request fails.
    [Fact]
    public async Task TwoDeletes_AtOnce_LeaveOneRequest_AndOneCommandRow()
    {
        Function function = await StoredRunningFunctionAsync();

        await Task.WhenAll(DeleteThroughTheHandlerAsync(function.Id), DeleteThroughTheHandlerAsync(function.Id));

        await using TitanDbContext read = pg.NewDbContext();
        Function stored = await new FunctionRepository(read).FindByIdAsync(function.Id) ?? throw new InvalidOperationException("gone");
        Assert.Equal(FunctionStatus.Deleting, stored.Status);
        ProviderCommand command = Assert.Single(await CommandsOfAsync(function.Id));
        Assert.Equal($"function-delete-{function.Id}-{stored.Operation.PendingId}", command.Key);
        Assert.Equal((ProviderCommandKinds.FunctionDelete, TestFunctions.Cluster, (Guid?)_tenant),
            (command.Kind, command.ClusterId, command.TenantId));
    }

    // A deploy whose command never reports, listed once it is older than the threshold, with its command row's status.
    [Fact]
    public async Task AStuckDeploy_IsListed_WithItsTenant_AndItsCommandRowsStatus()
    {
        Function function = TestFunctions.AFunction(_tenant, name: $"fn-{Guid.NewGuid():N}"[..12]);
        FunctionVersion version = function.WithVersion();
        Guid requestId = function.Deploy(version.Id);
        await using (TitanDbContext db = pg.NewDbContext())
        {
            db.Functions.Add(function);
            await new ProviderCommandQueue(new ProviderCommandRepository(db), TimeProvider.System)
                .EnqueueFunctionDeployAsync(function, version, requestId);
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE core.functions SET pending_request_asked_at = now() - make_interval(mins => 20) WHERE id = {function.Id}");
        }

        await using TitanDbContext read = pg.NewDbContext();
        IReadOnlyList<StuckRequest> stuck = await new PendingRequestReader(read).ListStuckAsync(TimeSpan.FromMinutes(15));

        StuckRequest deploy = Assert.Single(stuck, s => s.Id == function.Id);
        Assert.Equal(("function", (Guid?)_tenant, requestId, "Deploy", "Pending"),
            (deploy.Resource, deploy.TenantId, deploy.RequestId, deploy.Kind, deploy.CommandStatus));
    }

    // The worker's Done of a delete through the real repository and its lock: the function is Deleted, the view no longer
    // lists it, and its versions are now the cleanup's.
    [Fact]
    public async Task ADoneOfTheDelete_EndsTheRequest_AndLeavesEveryVersionToClean()
    {
        Function function = await StoredRunningFunctionAsync();
        await DeleteThroughTheHandlerAsync(function.Id);
        Guid requestId;
        await using (TitanDbContext db = pg.NewDbContext())
        {
            requestId = (await new FunctionRepository(db).FindByIdAsync(function.Id))!.Operation.PendingId!.Value;
        }

        await using (TitanDbContext db = pg.NewDbContext())
        {
            await new EndFunctionRequestCommandHandler(new FunctionRepository(db))
                .HandleAsync(new EndFunctionRequestCommand(function.Id, requestId, "Done"));
        }

        await using TitanDbContext read = pg.NewDbContext();
        Function stored = (await new FunctionRepository(read).FindByIdAsync(function.Id))!;
        Assert.Equal(FunctionStatus.Deleted, stored.Status);
        Assert.Contains(await new FunctionRepository(read).ListWithCodeToCleanAsync(), f => f.Id == function.Id);
        int waiting = await read.Database
            .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM core.pending_requests WHERE resource = 'function' AND id = {function.Id}")
            .SingleAsync();
        Assert.Equal(0, waiting);
    }

    // A version forgotten by the cleanup leaves the table: the row is removed, not orphaned.
    [Fact]
    public async Task ForgetCode_DeletesTheVersionsRow()
    {
        Function function = TestFunctions.AFunction(_tenant, name: $"fn-{Guid.NewGuid():N}"[..12]);
        FunctionVersion version = function.WithVersion();
        function.Delete();
        await using (TitanDbContext db = pg.NewDbContext())
        {
            db.Functions.Add(function);
            await db.SaveChangesAsync();
        }

        await using (TitanDbContext db = pg.NewDbContext())
        {
            var repo = new FunctionRepository(db);
            Function locked = (await repo.FindByIdForUpdateAsync(function.Id))!;
            locked.ForgetCode(version.Id);
            await repo.SaveAsync();
        }

        await using TitanDbContext read = pg.NewDbContext();
        Assert.False(await read.Set<FunctionVersion>().AnyAsync(v => v.Id == version.Id));
    }
}
