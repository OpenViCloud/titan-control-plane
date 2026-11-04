namespace Titan.Domain.Model;

/// The root of an aggregate: the one class of its folder that a repository loads and saves
/// (titan-ops architecture/code-structure.md#domain-base-classes). It carries nothing; guards read the role from it.
/// An interface, so a root on the provider can also take ProviderResource as its base.
public interface IAggregateRoot
{
}
