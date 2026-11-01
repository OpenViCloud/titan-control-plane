using Titan.Domain.Model;

namespace Titan.Domain.Aggregates.ClusterIpv6Pool;

/// One /64 block of a cluster that some network has held, named by its number in the cluster's /56. A row exists only
/// for a number ever held: no row means never held, which the pool gives out before any number given back. Only the
/// root reaches the methods below (titan-ops architecture/code-structure.md#child-entity).
public class Ipv6Block64Holding : ChildEntity
{
    public Guid PoolId { get; private set; }
    public int Number { get; private set; }
    /// The network holding the block; null once it was given back.
    public Guid? HolderNetworkId { get; private set; }
    /// When the block was given back; null while held, and for a block given back before the time was recorded
    /// (titan-ops features/network/design.md#ipv6-public: the oldest).
    public DateTimeOffset? GivenBackAt { get; private set; }

    public bool IsHeld => HolderNetworkId != null;

    private Ipv6Block64Holding() { }

    internal static Ipv6Block64Holding Held(Guid poolId, int number, Guid networkId)
    {
        return new Ipv6Block64Holding { PoolId = poolId, Number = number, HolderNetworkId = networkId };
    }

    internal static Ipv6Block64Holding GivenBack(Guid poolId, int number, DateTimeOffset? givenBackAt)
    {
        return new Ipv6Block64Holding { PoolId = poolId, Number = number, GivenBackAt = givenBackAt };
    }

    internal void HandTo(Guid networkId)
    {
        HolderNetworkId = networkId;
        GivenBackAt = null;
    }

    internal void TakeBack(DateTimeOffset at)
    {
        HolderNetworkId = null;
        GivenBackAt = at;
    }
}
