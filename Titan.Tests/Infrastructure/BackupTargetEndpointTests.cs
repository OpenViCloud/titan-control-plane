using FastEndpoints;
using Moq;
using Titan.Application.Abstractions;
using Titan.Application.Commands.Platform.BackupTarget;
using Titan.Application.Queries.Platform.BackupTarget;
using Titan.Domain.Repositories.Platform;
using Titan.Tests.Fakes;
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
        var handler = new Titan.Application.Queries.Platform.Backup.ListBackupsQueryHandler(
            Mock.Of<IBackupRecordRepository>(), Mock.Of<IBackupObservationRepository>(), TimeProvider.System);
        var ep = Factory.Create<ListBackupsEndpoint>(handler);

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
}
