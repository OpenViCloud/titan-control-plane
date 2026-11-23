using FastEndpoints;
using Moq;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Platform.BackupTarget;
using Titan.Application.Queries.Platform.BackupTarget;
using Titan.Domain.Repositories;
using Titan.Tests.Fakes;
using Titan.Domain.Aggregates.Vm;
using Titan.Domain.Aggregates.Volume;
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
        var ep = Factory.Create<ListBackupsEndpoint>(PostgresHandler(new FakeBackupRecordRepository()), DiskHandler(new(), new()), DrillHandler());

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

    private static Titan.Application.Queries.Admin.Backup.ListRestoreDrillsQueryHandler DrillHandler(
        params Titan.Domain.Aggregates.ProviderCommand.ProviderCommand[] drills)
    {
        var rows = new FakeProviderCommandRepository();
        rows.Rows.AddRange(drills);
        return new(rows, TimeProvider.System, new Titan.Application.Options.ClusterOptions
        {
            Clusters = [new Titan.Application.Options.ClusterConfig { Id = "mock", Name = "Mock" }],
        });
    }

    private static Titan.Application.Queries.Admin.Backup.ListDiskBackupsQueryHandler DiskHandler(
        List<Titan.Domain.Aggregates.Vm.Vm> vms, List<Titan.Domain.Aggregates.Volume.Volume> volumes)
    {
        var vmRepository = new Mock<Titan.Domain.Repositories.IVmRepository>();
        vmRepository.Setup(r => r.ListAllAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(vms);
        var volumeRepository = new Mock<Titan.Domain.Repositories.IVolumeRepository>();
        volumeRepository.Setup(r => r.ListBillableAsync(It.IsAny<CancellationToken>())).ReturnsAsync(volumes);
        return new(vmRepository.Object, volumeRepository.Object, new FakeBackupRecordRepository(),
            new FakeBackupObservationRepository(), TimeProvider.System, new Titan.Application.Options.ClusterOptions());
    }

    // One page for the platform: Postgres first, then every VM and volume Incus holds.
    [Fact]
    public async Task ListBackups_ReturnsPostgres_ThenTheVmsAndVolumes()
    {
        var vm = Titan.Domain.Aggregates.Vm.Vm.Create(TestTenant.WithRoomToSpare().Id, Guid.NewGuid(), "web-1", "m", "tpl-1", "mock", 20);
        vm.MarkRunning("vm-a", "10.0.0.2");
        var volume = Titan.Domain.Aggregates.Volume.Volume.Create(TestTenant.WithRoomToSpare().Id, "data-1", "mock", "std", 10);
        volume.MarkCreated("vol-a");
        var ep = Factory.Create<ListBackupsEndpoint>(PostgresHandler(new FakeBackupRecordRepository()), DiskHandler([vm], [volume]), DrillHandler());

        await ep.HandleAsync(CancellationToken.None);

        Assert.Equal(new[] { "postgres", "vm", "volume" }, ep.Response.Items.Select(i => i.Kind).ToArray());
        Assert.Equal(new[] { "titan-db", "web-1", "data-1" }, ep.Response.Items.Select(i => i.Name).ToArray());
    }

    // The Backups page's line for the latest restore drill rides on the same response.
    [Fact]
    public async Task ListBackups_ReturnsEachClustersLatestRestoreDrill_BesideTheItems()
    {
        var now = DateTimeOffset.UtcNow;
        var drill = Titan.Domain.Aggregates.ProviderCommand.ProviderCommand.Create(
            "restore-drill-mock-1", Titan.Domain.Aggregates.ProviderCommand.ProviderCommandKinds.RestoreDrill, "mock",
            """{"kind":"vm","name":"web-1","resourceId":"00000000-0000-0000-0000-000000000001"}""", now.AddHours(-1));
        var lease = Guid.NewGuid();
        drill.Lease(lease, now.AddHours(-1));
        drill.Complete(lease);
        var ep = Factory.Create<ListBackupsEndpoint>(PostgresHandler(new FakeBackupRecordRepository()), DiskHandler(new(), new()), DrillHandler(drill));

        await ep.HandleAsync(CancellationToken.None);

        var line = Assert.Single(ep.Response.RestoreDrills);
        Assert.Equal(("mock", "verified", "vm", "web-1"), (line.ClusterId, line.State, line.Kind, line.Name));
    }
}
