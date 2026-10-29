namespace Titan.Application.Commands.Networking.FirewallRule.Contracts;

// The route carries the network id; the endpoint's body record carries the rest (titan-ops architecture/code-structure.md#command-is-wire-contract).
public record CreateFirewallRuleCommand(
    Guid NetworkId,
    string Direction,
    string Protocol,
    int? StartPort,
    int? EndPort,
    string Cidr);
