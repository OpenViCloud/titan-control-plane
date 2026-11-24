using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Titan.Application.Commands.Admin.Backup;

namespace Titan.Tests.Api;

// The two restore routes of titan-ops features/backup/design.md#restore-as-new-resource on the host as Program.cs wires
// it: they exist and ask for a login, and their handlers resolve. Handlers are registered one line each, and no
// service-level test builds the host: a missing line is a 500 on the first restore an admin asks for.
//
// FullHost — WebApplicationFactory on its own Postgres database, own CI process. Reuses OpenApiSpecTests.ApiFactory.
[Trait("Category", "FullHost")]
[Collection("Http")]
public class RestoreFromBackupRouteTests : IClassFixture<OpenApiSpecTests.ApiFactory>
{
    private readonly OpenApiSpecTests.ApiFactory _factory;
    private readonly HttpClient _client;

    public RestoreFromBackupRouteTests(OpenApiSpecTests.ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Theory]
    [InlineData("vms")]
    [InlineData("volumes")]
    public async Task Restore_Exists_AndAsksForALogin(string resources)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/admin/backups/{resources}/{Guid.NewGuid()}/restore")
        {
            Content = JsonContent.Create(new { backupKey = "20261120T010000Z", name = "restored-1" }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        HttpResponseMessage response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public void BothHandlers_Resolve_FromTheHostsServices()
    {
        using IServiceScope scope = _factory.Services.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<RestoreVmFromBackupCommandHandler>());
        Assert.NotNull(scope.ServiceProvider.GetRequiredService<RestoreVolumeFromBackupCommandHandler>());
    }
}
