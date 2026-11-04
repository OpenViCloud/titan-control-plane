namespace Titan.Domain.Model;

/// An entity of the aggregate whose folder it sits in, and not that aggregate's root
/// (titan-ops architecture/code-structure.md#domain-base-classes).
/// It carries nothing; guards read the role from it. An interface, so an entity on the provider can also take
/// ProviderResource as its base.
public interface IEntity
{
}
