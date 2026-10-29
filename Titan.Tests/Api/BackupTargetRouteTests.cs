using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Titan.Application.Options;

namespace Titan.Tests.Api;

// The backup target is configuration, so the route that wrote it is gone: PUT answers 405 (the path is routed for GET and POST
// only), while the routes that remain exist and ask for a login. A half-done change (the old PUT still served) fails the first.
// titan-ops features/backup/user-stories.md#bak-01
//
// FullHost — WebApplicationFactory on its own Postgres database, own CI process. Reuses OpenApiSpecTests.ApiFactory.
[Trait("Category", "FullHost")]
[Collection("Http")]
public class BackupTargetRouteTests : IClassFixture<OpenApiSpecTests.ApiFactory>
{
    private readonly OpenApiSpecTests.ApiFactory _factory;
    private readonly HttpClient _client;

    public BackupTargetRouteTests(OpenApiSpecTests.ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // The env of the Deployment reaches the handlers: delete the binding in Program.cs and every environment answers 404.
    [Fact]
    public void TheDeploymentsEnv_IsBoundToTheTargetOfTheRealHost()
    {
        using var host = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("BackupTarget:Endpoint", "https://acct.r2.cloudflarestorage.com");
            builder.UseSetting("BackupTarget:Region", "auto");
            builder.UseSetting("BackupTarget:Bucket", "titan-backup-test");
            builder.UseSetting("BackupTarget:AccessKeyId", "key-id");
            builder.UseSetting("BackupTarget:SecretAccessKey", "the-secret");
        });

        var target = host.Services.GetRequiredService<IOptions<BackupTargetOptions>>().Value;

        Assert.Equal("titan-backup-test", target.Bucket);
        Assert.Equal("https://acct.r2.cloudflarestorage.com", target.Endpoint);
        Assert.True(target.HasKeyPair);
        Assert.Null(target.Problem());
    }


    [Fact]
    public async Task PutBackupTarget_IsGone_405()
    {
        var res = await _client.PutAsJsonAsync("/v1/admin/backup-target", new { endpoint = "https://x.example.com" });

        Assert.Equal(HttpStatusCode.MethodNotAllowed, res.StatusCode);
    }

    [Fact]
    public async Task GetBackupTarget_Exists_AndAsksForALogin()
    {
        var res = await _client.GetAsync("/v1/admin/backup-target");

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task TestBackupTarget_Exists_AndAsksForALogin()
    {
        var res = await _client.PostAsync("/v1/admin/backup-target/test", null);

        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }
}
