using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Titan.Domain.Aggregates.ApiToken;
using Titan.Domain.Aggregates.Network;
using Titan.Domain.Aggregates.Tenant;
using Titan.Infrastructure.Persistence;
using Titan.Tests.Infrastructure.Incus;

namespace Titan.Tests.Api;

/// POST /v1/tenant/networks through the host as Program.cs wires it, body binding included: a policy not sent is the
/// value of a new network (Deny inbound, Allow outbound), and Allow sent as 0, the way the provider sends it, stays
/// Allow (titan-ops features/network/design.md#firewall-default-policy).
[Trait("Category", "FullHost")]
[Collection("Http")]
public class NetworkCreateHostTests(NetworkCreateHostTests.OneClusterFactory factory)
    : IClassFixture<NetworkCreateHostTests.OneClusterFactory>
{
    // The cluster list and the identity Titan reaches it with are read while Program.cs builds the host, so they go in
    // as settings.
    public sealed class OneClusterFactory() : RealHostFactory(new Dictionary<string, string?>(), OneCluster())
    {
        private static Dictionary<string, string> OneCluster()
        {
            var identity = IncusTestKit.NewIdentity();
            return new Dictionary<string, string>
            {
                ["Clusters:0:Id"] = "cluster-a",
                ["Clusters:0:Name"] = "Cluster A",
                ["Clusters:0:Endpoint"] = "https://localhost:8443",
                ["Clusters:0:ServerFingerprint"] = "ffcf7803b2371e90834c2dd2a454f48bcb9c541b16dd7dc54a93a104b1aa108f",
                ["ProviderClientIdentity:CertificatePem"] = identity.CertificatePem,
                ["ProviderClientIdentity:KeyPem"] = identity.KeyPem,
            };
        }
    }

    private async Task<HttpClient> ClientOfANewTenantAsync()
    {
        Tenant tenant = Tenant.CreatePending("t-" + Guid.NewGuid().ToString("N")[..8], "kc-" + Guid.NewGuid());
        tenant.Approve();
        string secret = "ovc_" + Convert.ToBase64String(Guid.NewGuid().ToByteArray().Concat(Guid.NewGuid().ToByteArray()).ToArray())
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        ApiToken token = ApiToken.Create(tenant.Id, tenant.Members.Single().KeycloakUserId, "ci", secret, 90, DateTimeOffset.UtcNow);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TitanDbContext>();
            db.Tenants.Add(tenant);
            db.ApiTokens.Add(token);
            await db.SaveChangesAsync();
        }
        HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer " + secret);
        return client;
    }

    private async Task<(FirewallPolicy Ingress, FirewallPolicy Egress)> CreateAsync(object body)
    {
        HttpClient client = await ClientOfANewTenantAsync();
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/tenant/networks") { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        HttpResponseMessage response = await client.SendAsync(request);

        Assert.True(response.StatusCode == HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        var created = await response.Content.ReadFromJsonAsync<CreatedNetwork>();
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TitanDbContext>();
        Network stored = await db.Networks.SingleAsync(n => n.Id == created!.NetworkId);
        return (stored.DefaultIngressPolicy, stored.DefaultEgressPolicy);
    }

    private sealed record CreatedNetwork(Guid NetworkId);

    [Fact]
    public async Task PoliciesNotSent_DenyInbound_AndAllowOutbound()
    {
        var policies = await CreateAsync(new { name = "no-policy", clusterId = "cluster-a" });

        Assert.Equal((FirewallPolicy.Deny, FirewallPolicy.Allow), policies);
    }

    [Fact]
    public async Task AllowSentAsZero_IsAllow()
    {
        var policies = await CreateAsync(new { name = "allow-asked", clusterId = "cluster-a", defaultIngressPolicy = 0, defaultEgressPolicy = 0 });

        Assert.Equal((FirewallPolicy.Allow, FirewallPolicy.Allow), policies);
    }
}
