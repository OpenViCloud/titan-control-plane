using Microsoft.AspNetCore.Http;
using Titan.Infrastructure.Auth;
using FastEndpoints;
using Titan.Application.Commands.Networking.FirewallRule;
using Titan.Application.Commands.Networking.FirewallRule.Contracts;

namespace Titan.Infrastructure.Endpoints.Network;

public class CreateFirewallRuleEndpoint(CreateFirewallRuleCommandHandler handler)
    : Endpoint<CreateFirewallRuleRequest, CreateFirewallRuleCommandResult>, IRequiresIdempotencyKey
{
    public override void Configure()
    {
        Post("v1/tenant/networks/{networkId:guid}/firewall-rules");
        Description(b => b.ClearDefaultProduces(200).Produces<CreateFirewallRuleCommandResult>(201, "application/json"));
    }

    public override async Task HandleAsync(CreateFirewallRuleRequest req, CancellationToken ct)
    {
        var networkId = Route<Guid>("networkId");
        var result = await handler.HandleAsync(
            new CreateFirewallRuleCommand(networkId, req.Direction, req.Protocol, req.StartPort, req.EndPort, req.Cidr), ct);
        await SendAsync(result, 201, ct);
    }
}

// The JSON body; CreateFirewallRuleCommand additionally carries the route's network id (titan-ops architecture/code-structure.md#command-is-wire-contract).
public record CreateFirewallRuleRequest(
    string Direction,
    string Protocol,
    int? StartPort,
    int? EndPort,
    string Cidr);
