using Titan.Domain.Exceptions;

namespace Titan.Infrastructure.Validation;

/// Ipv4Cidr, FirewallCidr, NetworkName and PortRange each have one public way in, the factory that refuses
/// (titan-ops architecture/domain-methods.md#domain-method-shape). A validator or a read of stored text that has to
/// ask "would this build?" asks the factory.
public static class DomainValue
{
    public static T? OrNull<T>(Func<T> factory) where T : class
    {
        try
        {
            return factory();
        }
        catch (BusinessException)
        {
            return null;
        }
    }
}
