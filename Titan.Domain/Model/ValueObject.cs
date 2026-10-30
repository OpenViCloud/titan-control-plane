namespace Titan.Domain.Model;

/// A value with no identity, equal by its fields (titan-ops architecture/code-structure.md#domain-base-classes). A
/// record, so equality comes from the compiler; a subtype keeps its setters private, or `with` skips its factory.
public abstract record ValueObject;
