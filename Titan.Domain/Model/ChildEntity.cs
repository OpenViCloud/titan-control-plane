namespace Titan.Domain.Model;

/// An entity of the aggregate whose folder it sits in, and not that aggregate's root
/// (titan-ops architecture/code-structure.md#domain-base-classes).
/// It carries nothing; guards read the role from it.
public abstract class ChildEntity
{
}
