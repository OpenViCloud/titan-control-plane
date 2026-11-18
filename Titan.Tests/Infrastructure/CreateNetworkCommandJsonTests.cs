using System.Text.Json;
using Titan.Application.Commands.Networking.Network.Contracts;
using Titan.Domain.Aggregates.Network;

namespace Titan.Tests.Infrastructure;

// A policy not sent takes the value of a new network, and an explicit Allow (0 on the wire, as the provider sends it)
// stays Allow (titan-ops features/network/design.md#firewall-default-policy).
public class CreateNetworkCommandJsonTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void PoliciesNotSent_DenyInbound_AndAllowOutbound()
    {
        var command = JsonSerializer.Deserialize<CreateNetworkCommand>("""{"name":"n","clusterId":"c"}""", Web)!;

        Assert.Equal(FirewallPolicy.Deny, command.DefaultIngressPolicy);
        Assert.Equal(FirewallPolicy.Allow, command.DefaultEgressPolicy);
    }

    [Fact]
    public void AllowSentAsZero_IsAllow()
    {
        var command = JsonSerializer.Deserialize<CreateNetworkCommand>(
            """{"name":"n","clusterId":"c","defaultIngressPolicy":0,"defaultEgressPolicy":1}""", Web)!;

        Assert.Equal(FirewallPolicy.Allow, command.DefaultIngressPolicy);
        Assert.Equal(FirewallPolicy.Deny, command.DefaultEgressPolicy);
    }
}
