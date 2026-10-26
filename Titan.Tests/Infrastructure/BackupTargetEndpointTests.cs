using FastEndpoints;
using Moq;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Platform.BackupTarget;
using Titan.Application.Queries.Platform.BackupTarget;
using Titan.Domain.Repositories.Platform;
using Titan.Tests.Fakes;
using Titan.Domain.Aggregates.Compute.Vm;
using Titan.Infrastructure.Endpoints.Admin;

namespace Titan.Tests.Infrastructure;

// The backup bucket and its key are for platform-admin only. The global default only asks for a login, so the role lives in
// Configure(); these tests pin it so dropping or widening it fails a test instead of opening the endpoint to every tenant.
public class BackupTargetEndpointTests
{
    [Fact]
    public void Test_RequiresPlatformAdminRole_AndNoAnonymousAccess()
    {
        var handler = new TestBackupTargetCommandHandler(ConfiguredBackupTarget.Options(), Mock.Of<IBackupBucketFactory>());
        var ep = Factory.Create<TestBackupTargetEndpoint>(handler);

        Assert.NotNull(ep.Definition.AllowedRoles);
        Assert.Equal(new[] { "platform-admin" }, ep.Definition.AllowedRoles);
        Assert.Null(ep.Definition.AnonymousVerbs);
    }

    [Fact]
    public void ListBackups_RequiresPlatformAdminRole_AndNoAnonymousAccess()
    {
        var ep = Factory.Create<ListBackupsEndpoint>(PostgresHandler(new FakeBackupRecordRepository()), DiskHandler(new(), new()));

        Assert.NotNull(ep.Definition.AllowedRoles);
        Assert.Equal(new[] { "platform-admin" }, ep.Definition.AllowedRoles);
        Assert.Null(ep.Definition.AnonymousVerbs);
    }

    [Fact]
    public void Get_RequiresPlatformAdminRole_AndNoAnonymousAccess()
    {
        var ep = Factory.Create<GetBackupTargetEndpoint>(new GetBackupTargetQueryHandler(ConfiguredBackupTarget.Options()));

        Assert.NotNull(ep.Definition.AllowedRoles);
        Assert.Equal(new[] { "platform-admin" }, ep.Definition.AllowedRoles);
        Assert.Null(ep.Definition.AnonymousVerbs);
    }

    private static Titan.Application.Queries.Platform.Backup.ListBackupsQueryHandler PostgresHandler(FakeBackupRecordRepository records) =>
        new(records, new FakeBackupObservationRepository(), TimeProvider.System);

    private static Titan.Application.Queries.Admin.Backup.ListDiskBackupsQueryHandler DiskHandler(
        List<Titan.Domain.Aggregates.Compute.Vm.Vm> vms, List<Titan.Domain.Aggregates.Compute.Volume.Volume> volumes)
    {
        var vmRepository = new Mock<Titan.Domain.Repositories.Compute.IVmRepository>();
        vmRepository.Setup(r => r.ListAllAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(vms);
        var volumeRepository = new Mock<Titan.Domain.Repositories.Compute.IVolumeRepository>();
        volumeRepository.Setup(r => r.ListBillableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(volumes);
        return new(vmRepository.Object, volumeRepository.Object, new FakeBackupRecordRepository(),
            new FakeBackupObservationRepository(), TimeProvider.System);
    }

    // One page for the platform: Postgres first, then every VM and volume Incus holds.
    [Fact]
    public async Task ListBackups_ReturnsPostgres_ThenTheVmsAndVolumes()
    {
        var vm = Titan.Domain.Aggregates.Compute.Vm.Vm.CreateUnchecked(Guid.NewGuid(), Guid.NewGuid(), "web-1", "m", "tpl-1", "mock", 20);
        vm.MarkRunning("vm-a", "10.0.0.2");
        var volume = Titan.Domain.Aggregates.Compute.Volume.Volume.CreateUnchecked(Guid.NewGuid(), "data-1", "mock", "std", 10);
        volume.MarkCreated("vol-a");
        var ep = Factory.Create<ListBackupsEndpoint>(PostgresHandler(new FakeBackupRecordRepository()), DiskHandler([vm], [volume]));

        await ep.HandleAsync(CancellationToken.None);

        Assert.Equal(new[] { "postgres", "vm", "volume" }, ep.Response.Items.Select(i => i.Kind).ToArray());
        Assert.Equal(new[] { "titan-db", "web-1", "data-1" }, ep.Response.Items.Select(i => i.Name).ToArray());
    }
}
