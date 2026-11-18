using Microsoft.EntityFrameworkCore;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Compute.VmKeyAddition;
using Titan.Application.Commands.Compute.VmKeyAddition.Contracts;
using Titan.Application.Services.Platform.ActivityEvent;
using Titan.Domain.Aggregates.SshKey;
using Titan.Domain.Aggregates.Vm;
using Titan.Infrastructure.Persistence;
using Titan.Infrastructure.ProviderCommands;
using Titan.Infrastructure.Repositories;
using Titan.Tests.Domain;

namespace Titan.Tests.Infrastructure.Integration;

/// What only Postgres can show of a key addition: the ssh_key_ids array round-trips, and a
/// waiting one surfaces in core.pending_requests the same way every other lifecycle table's waiting row does
/// (titan-ops architecture/resource-lifecycle.md#lost-and-stale). Category=Integration — its own CI step.
[Trait("Category", "Integration")]
[Collection(PostgresCollection.Name)]
public class VmKeyAdditionRequestsDbTests(PostgresFixture pg)
{
    private const string Offering = "m-key-addition";

    // A fresh tenant per test: two tests sharing one tenant would collide on the ssh_keys fingerprint's unique
    // index, since both store the same test key material.
    private static Guid NewTenant() => Guid.NewGuid();

    private static Vm StoredVm(Guid tenantId) =>
        Vm.Create(tenantId, Guid.NewGuid(), $"vm-{Guid.NewGuid():N}"[..14], Offering, "tpl", "cluster-a", 20);

    private async Task<Vm> StoredRunningVmAsync(Guid tenantId)
    {
        Vm vm = StoredVm(tenantId);
        vm.MarkRunning($"ext-{vm.Id:N}", "10.0.0.5");
        await using TitanDbContext db = pg.NewDbContext();
        await db.Database.ExecuteSqlRawAsync(
            $"INSERT INTO core.vm_offerings (id, name, vcpu_count, memory_mb, price_per_hour) VALUES ('{Offering}', 'm', 2, 4096, 311.256) ON CONFLICT (id) DO NOTHING");
        db.Vms.Add(vm);
        await db.SaveChangesAsync();
        return vm;
    }

    private async Task<SshKey> StoredKeyAsync(Guid tenantId)
    {
        var key = SshKey.Create(tenantId, "laptop", SshPublicKeyTests.Ed25519);
        await using TitanDbContext db = pg.NewDbContext();
        db.SshKeys.Add(key);
        await db.SaveChangesAsync();
        return key;
    }

    private async Task<(Guid KeyAdditionId, Guid RequestId)> AddKeysThroughTheHandlerAsync(Guid tenantId, Guid vmId, Guid sshKeyId)
    {
        await using TitanDbContext db = pg.NewDbContext();
        VmRepository vms = new(db);
        ICurrentUser caller = Callers.TenantMember(tenantId);
        var handler = new CreateVmKeyAdditionCommandHandler(vms, new SshKeyRepository(db), caller,
            new ProviderCommandQueue(new ProviderCommandRepository(db), TimeProvider.System),
            new ActivityRecorder(new ActivityEventRepository(db), caller));
        var result = await handler.HandleAsync(new CreateVmKeyAdditionCommand(vmId, [sshKeyId]));
        Vm vm = (await vms.FindByIdAsync(vmId))!;
        VmKeyAddition addition = vm.KeyAdditions.Single(a => a.Id == result.KeyAdditionId);
        return (addition.Id, addition.PendingId!.Value);
    }

    [Fact]
    public async Task TheSshKeyIds_RoundTripThroughPostgres()
    {
        Guid tenant = NewTenant();
        Vm vm = await StoredRunningVmAsync(tenant);
        SshKey key = await StoredKeyAsync(tenant);

        (Guid keyAdditionId, _) = await AddKeysThroughTheHandlerAsync(tenant, vm.Id, key.Id);

        await using TitanDbContext read = pg.NewDbContext();
        var repo = new VmKeyAdditionRepository(read);
        VmKeyAddition? found = await repo.FindByIdAsync(keyAdditionId);

        Assert.NotNull(found);
        Assert.Equal([key.Id], found!.SshKeyIds);
    }

    [Fact]
    public async Task AWaitingKeyAddition_IsListedInPendingRequests_WithItsCommandRowsKey()
    {
        Guid tenant = NewTenant();
        Vm vm = await StoredRunningVmAsync(tenant);
        SshKey key = await StoredKeyAsync(tenant);

        (Guid keyAdditionId, Guid requestId) = await AddKeysThroughTheHandlerAsync(tenant, vm.Id, key.Id);

        await using TitanDbContext db = pg.NewDbContext();
        List<string> rows = await db.Database
            .SqlQuery<string>($"SELECT resource || '|' || kind || '|' || request_id::text AS \"Value\" FROM core.pending_requests WHERE id = {keyAdditionId}")
            .ToListAsync();
        Assert.Equal([$"vm-key-addition|Create|{requestId}"], rows);

        List<string> keys = await db.Database
            .SqlQuery<string>($"SELECT key AS \"Value\" FROM core.provider_commands WHERE key = {$"vm-key-addition-create-{keyAdditionId}-{requestId}"}")
            .ToListAsync();
        Assert.Single(keys);
    }
}
