namespace Titan.Domain.Aggregates.ClusterIpv6Pool;

/// What ClusterIpv6Pool.GiveBlock64To hands the network: the number of the /64 block it now holds, the block itself and
/// the router address OVH routes it to (titan-ops infra/incus/design.md#ipv6-address-plan). Not stored: the network keeps
/// the two addresses, the pool keeps the number.
public sealed record GrantedBlock64(int Number, string Subnet, string RouterAddress);
