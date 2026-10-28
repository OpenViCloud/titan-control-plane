using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Titan.Application.Commands.Admin.Backup;
using Titan.Infrastructure.Hosting;

namespace Titan.Tests.Api;

/// <summary>
/// The backup schedule runs only from the host: a handler missing from Program.cs fails once an hour in a log line, and no
/// service-level test builds the host.
/// </summary>
// FullHost — its own CI process, for the reason CatalogContractTests gives.
[Trait("Category", "FullHost")]
[Collection("Http")]
public class BackupHostWiringTests : IClassFixture<OpenApiSpecTests.ApiFactory>
{
    private readonly OpenApiSpecTests.ApiFactory _factory;

    public BackupHostWiringTests(OpenApiSpecTests.ApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public void TheScheduleRunsInTheHost_AndItsHandlerResolves()
    {
        Assert.Contains(_factory.Services.GetServices<IHostedService>(), s => s is BackupScheduleService);

        using IServiceScope scope = _factory.Services.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<ScheduleBackupsCommandHandler>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<Titan.Application.Queries.Admin.Backup.ListDiskBackupsQueryHandler>());
    }

    // The same holds for the template sync a new environment needs before its first VM.
    [Fact]
    public void TheNeverAskedTemplateSyncRunsInTheHost_AndItsHandlerResolves()
    {
        Assert.Contains(_factory.Services.GetServices<IHostedService>(), s => s is TemplateSyncNeverAskedService);

        using IServiceScope scope = _factory.Services.CreateScope();
        Assert.NotNull(scope.ServiceProvider
            .GetRequiredService<Titan.Application.Queries.Catalog.VmTemplate.ListClustersWithNeverAskedTemplatesQueryHandler>());
    }
}
