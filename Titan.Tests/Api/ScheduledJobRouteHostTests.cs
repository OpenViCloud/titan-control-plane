using System.Net;
using System.Net.Http.Headers;

namespace Titan.Tests.Api;

/// POST /internal/jobs/{job} as each job's CronJob, and the IPv6 route seed's PostSync Job, send it: the token header,
/// a JSON content type and an empty object. The route binds a request object, so the host answers a POST without a JSON
/// content type with 415 before any pass runs; a test calling HandleAsync never reaches that
/// (titan-ops architecture/background-jobs.md#background-jobs).
// FullHost — its own CI process, for the reason CatalogContractTests gives.
[Trait("Category", "FullHost")]
[Collection("Http")]
public class ScheduledJobRouteHostTests(ScheduledJobRouteHostTests.JobHostFactory factory)
    : IClassFixture<ScheduledJobRouteHostTests.JobHostFactory>
{
    private const string InternalToken = "test-token";

    // The curl of titan-local-cluster base/control-plane/scheduled-jobs.yaml, spelled out here.
    private async Task<HttpResponseMessage> CallAsCronJobAsync(string job, string? token = InternalToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/internal/jobs/{job}")
        {
            Content = new StringContent("{}", new MediaTypeHeaderValue("application/json"))
        };
        if (token != null)
        {
            request.Headers.Add("X-Internal-Token", token);
        }
        return await factory.CreateClient().SendAsync(request);
    }

    [Fact]
    public async Task TheMeterCronJobsCall_RunsAPass_AndAnswers204()
    {
        HttpResponseMessage response = await CallAsCronJobAsync("meter");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    // The wallet sweeper's CronJob calls every 2 minutes; a 415 or 404 here would leave every lost notify Pending.
    [Fact]
    public async Task TheWalletSweepCronJobsCall_RunsAPass_AndAnswers204()
    {
        HttpResponseMessage response = await CallAsCronJobAsync("wallet-sweep");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    // Why the CronJob sends an empty object: the call without it never reaches the pass.
    [Fact]
    public async Task ACallWithoutAJsonBody_Is415()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/internal/jobs/meter");
        request.Headers.Add("X-Internal-Token", InternalToken);

        HttpResponseMessage response = await factory.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task ANameNoJobHas_Is404()
    {
        HttpResponseMessage response = await CallAsCronJobAsync("no-such-job");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ACallWithoutTheToken_Is401()
    {
        HttpResponseMessage response = await CallAsCronJobAsync("meter", token: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    public sealed class JobHostFactory() : RealHostFactory(
        new Dictionary<string, string?> { ["Internal:Token"] = InternalToken }, new Dictionary<string, string>());
}
