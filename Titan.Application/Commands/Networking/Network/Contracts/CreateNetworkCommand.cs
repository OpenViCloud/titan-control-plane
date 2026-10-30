using Titan.Domain.Aggregates.Network;

namespace Titan.Application.Commands.Networking.Network.Contracts;

// The wire contract of POST /v1/tenant/networks (titan-ops architecture/code-structure.md#command-is-wire-contract).
public record CreateNetworkCommand(
    string Name,
    string ClusterId,
    string? Cidr = null,
    FirewallPolicy DefaultIngressPolicy = FirewallPolicy.Allow,
    FirewallPolicy DefaultEgressPolicy = FirewallPolicy.Allow,
    // One public /64 of the cluster for the network; every VM on it gets an address in it
    // (titan-ops infra/incus/design.md#ipv6-address-plan).
    bool EnablePublicIpv6 = false);
